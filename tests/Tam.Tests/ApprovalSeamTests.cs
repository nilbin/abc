using System.Text.Json;
using Erp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

/// <summary>
/// The three approvals seams (docs/28 D-AG4, tutorial Step 16), proven through the REAL pipeline
/// on SQLite: (1) a wildcard gate sees every operation and decides from its own data; (2) work a
/// blocking gate parks commits in a fresh scope while the domain write rolls back; (3) a parked
/// envelope replays as its ORIGINAL initiator — fresh grants, Workflow source, correlation into
/// audit, idempotent under redelivery.
/// </summary>
public class ApprovalSeamTests : IDisposable
{
    private const string Tenant = "t1";
    private static readonly Guid Initiator = Guid.Parse("6d5a3f00-0000-0000-0000-000000000001");

    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _services;
    private readonly TamModel _model;

    public ApprovalSeamTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new TenantStampInterceptor())
            .Options;

        _model = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["operations.host.customers.create.title"] = "Create customer",
                ["labels.name"] = "Name",
            })
            .AddOperationType(typeof(CreateCustomer))
            .AddPlugin<ParkingPlugin>()
            .Build();

        _services = new ServiceCollection()
            .AddSingleton(_model)
            .AddScoped<TenantScope>()
            .AddScoped(sp => new ErpDbContext(options, sp.GetRequiredService<TenantScope>()))
            .AddScoped<ITamDb>(sp => new TamDb(sp.GetRequiredService<ErpDbContext>()))
            .AddScoped<ActivationCache>()
            .AddScoped<ITamActivator, TamActivator>()
            .AddScoped(sp => new OperationExecutor(_model, sp, s => s.GetRequiredService<ErpDbContext>()))
            .AddSingleton(sp => new EnvelopeReplay(_model, sp, s => s.GetRequiredService<ErpDbContext>()))
            .BuildServiceProvider();

        using var db = new ErpDbContext(options, new TenantScope());
        db.Database.EnsureCreated();
        db.Add(new PluginActivationEntity { Id = Guid.NewGuid(), TenantId = Tenant, PluginId = "appr" });
        db.Add(new AccountEntity { Id = Initiator, Email = "init@x", DisplayName = "Init", Active = true });
        db.Add(new TenantMembershipEntity
        {
            Id = Guid.NewGuid(), TenantId = Tenant, AccountId = Initiator,
            RolesJson = """["clerk"]""", Active = true,
        });
        db.Add(new RoleEntity
        {
            Id = Guid.NewGuid(), TenantId = Tenant, Name = "clerk",
            PermissionsJson = """["host.customers.create"]""",
        });
        db.SaveChanges();
    }

    // ---- the tiny host + plugin under test -------------------------------------------------

    [Operation("host.customers.create")]
    [Authorize("host.customers.create")]
    private static class CreateCustomer
    {
        public sealed record Input([property: LabelKey("labels.name")] string Name);

        public static Task<Result> Execute(Input input, OperationContext context, ITamDb tam)
        {
            tam.Db.Add(Customer.Create(
                context.TenantId.Value, new(input.Name), new("Road 1"), null, null));
            return Task.FromResult(Result.Success());
        }
    }

    [TamPlugin("appr")]
    private sealed class ParkingPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.appr.title"] = "Approvals",
                ["appr.parked"] = "Parked for approval.",
            });
            // Seam 1: ONE wildcard registration — which operations it actually blocks is the
            // gate's own runtime decision, standing in for tenant-configured approval rules.
            plugin.GateAll<ParkingGate>();
        }
    }

    private sealed class ParkingGate : IOperationGate
    {
        public Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
        {
            // Seam 3's sanction: a replay released by an approver passes the gate.
            if (gate.Context.Source == InvocationSource.Workflow)
                return Task.FromResult(Result.Success());
            if (gate.OperationId != "host.customers.create"
                || !gate.Input.TryGetProperty("name", out var name)
                || name.GetString() is not { } value)
                return Task.FromResult(Result.Success());

            // Stands in for the plugin's own envelope table: key by the pipeline's payload
            // hash (the idempotency hash), store the raw wire body + initiator for replay.
            var envelope = new ParkedEnvelope(
                gate.OperationId, gate.Input.GetRawText(), gate.PayloadHash,
                gate.Context.Actor.Id, gate.Context.Culture);

            if (value.StartsWith("SOFT", StringComparison.Ordinal))
            {
                // Parks but ALLOWS — the parked work must be discarded (nothing may leak
                // from a gate that ends up letting the operation through).
                gate.Park<ParkEnvelopeWork, ParkedEnvelope>(envelope);
                return Task.FromResult(Result.Success());
            }

            if (!value.StartsWith("BLOCK", StringComparison.Ordinal))
                return Task.FromResult(Result.Success());

            // Seam 2: keep the envelope, lose the attempt.
            gate.Park<ParkEnvelopeWork, ParkedEnvelope>(envelope);
            return Task.FromResult((Result)Finding.Error("appr.parked").Create());
        }
    }

    // Constructed by the pipeline IN the fresh scope: the injected ITamDb is the fresh context
    // by construction — the rolled-back gate scope is structurally unreachable from here.
    private sealed class ParkEnvelopeWork(ITamDb tam) : IParkedWork<ParkedEnvelope>
    {
        public async Task RunAsync(ParkedEnvelope envelope, CancellationToken ct)
        {
            tam.Db.Add(new TenantSettingEntity
            {
                Id = Guid.NewGuid(),
                Key = "appr.envelope." + envelope.PayloadHash,
                Value = JsonSerializer.Serialize(envelope),
            });
            await tam.Db.SaveChangesAsync(ct);
        }
    }

    private sealed record ParkedEnvelope(
        string OperationId, string BodyJson, string PayloadHash, string ActorId, string Culture);

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<OperationResponse> ExecuteAsync(string name, Actor? actor = null)
    {
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Current = Tenant;
        var context = new OperationContext
        {
            Actor = actor ?? new Actor(
                Initiator.ToString(), "Init", new HashSet<string> { "host.customers.create" }),
            TenantId = new TenantId(Tenant),
            Source = InvocationSource.Web,
            Culture = "en",
            Services = scope.ServiceProvider,
        };
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(new { name }));
        return await scope.ServiceProvider.GetRequiredService<OperationExecutor>()
            .ExecuteAsync("host.customers.create", body.RootElement.Clone(), context, CancellationToken.None);
    }

    private T Query<T>(Func<ErpDbContext, T> read)
    {
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Current = Tenant;
        return read(scope.ServiceProvider.GetRequiredService<ErpDbContext>());
    }

    private ParkedEnvelope SingleEnvelope() => Query(db =>
    {
        var row = db.Set<TenantSettingEntity>().Single(x => x.Key.StartsWith("appr.envelope."));
        return JsonSerializer.Deserialize<ParkedEnvelope>(row.Value)!;
    });

    // ---- seam 1 + 2: the wildcard gate parks across the rollback ----------------------------

    [Fact]
    public async Task A_blocking_gate_parks_the_envelope_while_the_domain_write_rolls_back()
    {
        var response = await ExecuteAsync("BLOCK Acme");

        Assert.Contains(response.Findings, f => f.Code == "appr.parked");
        // The attempt itself rolled back — no customer, no audit of a write that never happened.
        Assert.Equal(0, Query(db => db.Customers.Count()));
        // ...but the envelope survived, committed by the fresh pinned scope, stamped with the tenant.
        var envelope = SingleEnvelope();
        Assert.Equal("host.customers.create", envelope.OperationId);
        Assert.Equal(Initiator.ToString(), envelope.ActorId);
        Assert.Equal(Tenant, Query(db =>
            db.Set<TenantSettingEntity>().Single(x => x.Key.StartsWith("appr.envelope.")).TenantId));
    }

    [Fact]
    public async Task Parked_work_is_discarded_when_the_gate_allows_the_operation()
    {
        var response = await ExecuteAsync("SOFT Acme");

        Assert.DoesNotContain(response.Findings, f => f.Severity == FindingSeverity.Error);
        Assert.Equal(1, Query(db => db.Customers.Count()));
        Assert.Equal(0, Query(db =>
            db.Set<TenantSettingEntity>().Count(x => x.Key.StartsWith("appr.envelope."))));
    }

    [Fact]
    public async Task An_operation_the_gate_ignores_runs_untouched()
    {
        var response = await ExecuteAsync("Plain Acme");
        Assert.DoesNotContain(response.Findings, f => f.Severity == FindingSeverity.Error);
        Assert.Equal(1, Query(db => db.Customers.Count()));
    }

    // ---- seam 3: sanctioned replay ----------------------------------------------------------

    [Fact]
    public async Task Replay_runs_as_the_initiator_with_workflow_source_and_correlated_audit()
    {
        await ExecuteAsync("BLOCK Acme");
        var envelope = SingleEnvelope();

        using var body = JsonDocument.Parse(envelope.BodyJson);
        var replay = _services.GetRequiredService<EnvelopeReplay>();
        var response = await replay.ReplayAsync(new EnvelopeReplay.Envelope(
            envelope.OperationId, body.RootElement, envelope.ActorId,
            Tenant, "env-1", envelope.Culture), CancellationToken.None);

        // The gate saw Workflow and let it pass; the operation ran with the initiator's grants
        // as re-resolved from the seeded membership (clerk → host.customers.create).
        Assert.DoesNotContain(response.Findings, f => f.Severity == FindingSeverity.Error);
        Assert.Equal(1, Query(db => db.Customers.Count()));

        // Dual attribution: the audit's actor IS the initiator; the envelope id rides the
        // correlation (the approver's own audit lives on the plugin's approve operation).
        var audit = Query(db => db.Set<AuditEntry>()
            .Single(a => a.OperationId == "host.customers.create"));
        Assert.Equal(Initiator.ToString(), audit.ActorId);
        Assert.Equal(nameof(InvocationSource.Workflow), audit.Source);
        Assert.Equal("env-1", audit.CorrelationId);
        Assert.Equal(EnvelopeReplay.KeyPrefix + "env-1", audit.IdempotencyKey);

        // Redelivered approval effect → the stored outcome, not a second execution.
        using var again = JsonDocument.Parse(envelope.BodyJson);
        var second = await replay.ReplayAsync(new EnvelopeReplay.Envelope(
            envelope.OperationId, again.RootElement, envelope.ActorId,
            Tenant, "env-1", envelope.Culture), CancellationToken.None);
        Assert.Contains(second.Findings, f => f.Code == "pipeline.idempotent-replay");
        Assert.Equal(1, Query(db => db.Customers.Count()));
    }

    [Fact]
    public async Task Replay_fails_closed_when_the_initiator_is_deactivated_or_stripped()
    {
        await ExecuteAsync("BLOCK Acme");
        var envelope = SingleEnvelope();
        Query(db =>
        {
            var account = db.Set<AccountEntity>().Single(a => a.Id == Initiator);
            account.Active = false;
            return db.SaveChanges();
        });

        using var body = JsonDocument.Parse(envelope.BodyJson);
        var response = await _services.GetRequiredService<EnvelopeReplay>().ReplayAsync(
            new EnvelopeReplay.Envelope(
                envelope.OperationId, body.RootElement, Initiator.ToString(),
                Tenant, "env-2", envelope.Culture), CancellationToken.None);

        Assert.Contains(response.Findings, f => f.Code == "pipeline.replay-actor-unavailable");
        Assert.Equal(0, Query(db => db.Customers.Count()));
    }

    [Fact]
    public void A_wildcard_gate_registers_without_naming_any_host_operation()
    {
        Assert.Single(_model.Gates[GateDefinition.Wildcard]);
        Assert.Equal("appr", _model.Gates[GateDefinition.Wildcard][0].PluginId);
    }

    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
    }
}
