using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Testing;

/// <summary>
/// The in-process pipeline host (tutorial Step 11): runs the REAL executors — authorization,
/// structural validation, gates, transaction, merge, audit, outbox — against a real database
/// provider, with no HTTP in the loop. What is green here is what is true on the wire, because
/// it is the same pipeline.
/// </summary>
public sealed class TamTestHost<TDb> : IAsyncDisposable where TDb : DbContext
{
    private readonly ServiceProvider services;
    private readonly SqliteConnection? ownedConnection;

    public TamModel Model { get; }

    private TamTestHost(TamModel model, ServiceProvider services, SqliteConnection? ownedConnection)
    {
        Model = model;
        this.services = services;
        this.ownedConnection = ownedConnection;
    }

    /// <summary>Host over any provider: pass the same DbContext options the app uses
    /// (UseNpgsql for provider-true CI, UseSqlite for local runs). The schema is created
    /// via EnsureCreated unless <paramref name="createSchema"/> is false.</summary>
    public static async Task<TamTestHost<TDb>> CreateAsync(
        TamModel model,
        Action<DbContextOptionsBuilder> configureDb,
        Action<IServiceCollection>? configureServices = null,
        bool createSchema = true,
        SqliteConnection? ownedConnection = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDbContext<TDb>(options =>
        {
            configureDb(options);
            options.UseTamConventions();
        });
        collection.AddTam<TDb>(model);
        configureServices?.Invoke(collection);
        var services = collection.BuildServiceProvider();

        if (createSchema)
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TDb>().Database.EnsureCreatedAsync();
        }
        return new TamTestHost<TDb>(model, services, ownedConnection);
    }

    /// <summary>The one-liner: an isolated in-memory SQLite database (schema created), gone on
    /// dispose. Provider-true against PostgreSQL is the CreateAsync overload with UseNpgsql.</summary>
    public static Task<TamTestHost<TDb>> CreateSqliteAsync(
        TamModel model, Action<IServiceCollection>? configureServices = null)
    {
        // A shared-cache in-memory database lives exactly as long as one connection stays
        // open; the host holds that anchor and closes it on dispose.
        var anchor = new SqliteConnection($"DataSource=tamtest-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        anchor.Open();
        return CreateAsync(model, options => options.UseSqlite(anchor.ConnectionString),
            configureServices, createSchema: true, ownedConnection: anchor);
    }

    /// <summary>An actor for test calls. Permissions are the flat grant set — paired atoms and
    /// "*" behave exactly as in production (reserved atoms still need explicit naming).</summary>
    public TestActor<TDb> Actor(string tenantId, params string[] permissions) =>
        new(this, tenantId, new Actor(
            Guid.NewGuid().ToString(), "test-actor", new HashSet<string>(permissions)));

    /// <summary>An actor with a FIXED id — for own-scope tests where a row's owner must be
    /// this actor (paired-atom base without the -all twin).</summary>
    public TestActor<TDb> ActorWithId(string tenantId, string actorId, params string[] permissions) =>
        new(this, tenantId, new Actor(actorId, "test-actor", new HashSet<string>(permissions)));

    /// <summary>Seed or inspect data in an ambient tenant scope — the same global filter and
    /// stamping the pipeline sees. Changes are saved on return.</summary>
    public async Task SeedAsync(string tenantId, Func<TDb, Task> seed)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Current = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<TDb>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Dispatches every due outbox row NOW, exactly as the production background dispatcher
    /// would (claim-lease, per-record tenant pinning, plugin activation gating, poison
    /// isolation) — the harness runs no background services, so subscriber effects only happen
    /// when a test asks for them. Deterministic by design: call it after the operation that
    /// published the event, then assert the subscriber's writes. Loops until a pass moves
    /// nothing (rows that failed and are inside their retry lease stay pending — like
    /// production, delivery is at-least-once and subscribers are idempotent). Returns the
    /// number of rows dispatched or dead-lettered.
    /// </summary>
    public async Task<int> DispatchOutboxAsync(CancellationToken ct = default)
    {
        var dispatcher = new OutboxDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDb>(),
            Model);
        var total = 0;
        while (true)
        {
            var finished = await dispatcher.DispatchPendingAsync(ct);
            if (finished == 0) return total;
            total += finished;
        }
    }

    /// <summary>Read-side twin of <see cref="SeedAsync"/>.</summary>
    public async Task<T> QueryDbAsync<T>(string tenantId, Func<TDb, Task<T>> query)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Current = tenantId;
        return await query(scope.ServiceProvider.GetRequiredService<TDb>());
    }

    internal async Task<OperationResponse> ExecuteAsync(
        TestActor<TDb> actor, string operationId, object input, string? idempotencyKey,
        CancellationToken ct, string? formId = null)
    {
        await using var scope = services.CreateAsyncScope();
        var context = BuildContext(scope.ServiceProvider, actor, idempotencyKey);
        var body = JsonSerializer.SerializeToElement(input, TamJson.Options);
        return await scope.ServiceProvider.GetRequiredService<OperationExecutor>()
            .ExecuteAsync(operationId, body, context, ct, formId);
    }

    /// <summary>Runs an operation and then a caller assertion against the SAME scope and DbContext
    /// the operation ran on — the shared-scope shape a non-request caller has. Lets a test observe
    /// that a blocked operation left no tracked residue on its context (the Finding 1 regression):
    /// a normal <see cref="TestActor{TDb}.ExecuteAsync"/> disposes its scope before the test can
    /// look.</summary>
    public async Task<T> ExecuteThenInspectAsync<T>(
        TestActor<TDb> actor, string operationId, object input,
        Func<OperationResponse, TDb, Task<T>> inspect, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = BuildContext(scope.ServiceProvider, actor, idempotencyKey: null);
        var body = JsonSerializer.SerializeToElement(input, TamJson.Options);
        var response = await scope.ServiceProvider.GetRequiredService<OperationExecutor>()
            .ExecuteAsync(operationId, body, context, ct);
        return await inspect(response, scope.ServiceProvider.GetRequiredService<TDb>());
    }

    internal async Task<(ViewResponse? Response, Finding? Error)> QueryAsync(
        TestActor<TDb> actor, string viewId, IReadOnlyDictionary<string, string?> query, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var context = BuildContext(scope.ServiceProvider, actor, idempotencyKey: null);
        return await scope.ServiceProvider.GetRequiredService<ViewExecutor>()
            .ExecuteAsync(viewId, query, context, ct);
    }

    private OperationContext BuildContext(IServiceProvider scoped, TestActor<TDb> actor, string? idempotencyKey)
    {
        // The same pinning MapTamAuth does per request: context, actor and DbContext filter
        // agree on the acting node before anything executes.
        scoped.GetRequiredService<TenantScope>().Current = actor.TenantId;
        return new OperationContext
        {
            Actor = actor.Value,
            TenantId = new TenantId(actor.TenantId),
            Source = InvocationSource.Internal,
            Culture = Model.DefaultCulture,
            IdempotencyKey = idempotencyKey,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Services = scoped,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        if (ownedConnection is not null) await ownedConnection.DisposeAsync();
    }
}

/// <summary>A (tenant, grant-set) pair calls are made as. Cheap value — mint one per scenario.</summary>
public sealed class TestActor<TDb> where TDb : DbContext
{
    private readonly TamTestHost<TDb> host;
    internal string TenantId { get; }
    internal Actor Value { get; }

    internal TestActor(TamTestHost<TDb> host, string tenantId, Actor value)
    {
        this.host = host;
        TenantId = tenantId;
        Value = value;
    }

    /// <summary>Executes an operation through the full pipeline. <paramref name="input"/> is any
    /// object whose JSON matches the operation's Input record (anonymous objects work; Change
    /// fields are <c>new { original = ..., value = ... }</c>).</summary>
    public Task<OperationResponse> ExecuteAsync(
        string operationId, object input, string? idempotencyKey = null, CancellationToken ct = default) =>
        host.ExecuteAsync(this, operationId, input, idempotencyKey, ct);

    /// <summary>Submits THROUGH a named form binding (docs/40): the form's tightening applies on
    /// top of the operation contract. The direct <see cref="ExecuteAsync(string, object, string?,
    /// CancellationToken)"/> overload omits it — that's the door MCP and integrations use.</summary>
    public Task<OperationResponse> ExecuteThroughFormAsync(
        string formId, string operationId, object input, CancellationToken ct = default) =>
        host.ExecuteAsync(this, operationId, input, idempotencyKey: null, ct, formId);

    /// <summary>Executes a view with wire-shaped query parameters
    /// (<c>sort</c>/<c>dir</c>/<c>page</c>/<c>pageSize</c>, filters as <c>field</c>,
    /// <c>field.from</c>, <c>field.to</c>, <c>field.contains</c>, extension fields as
    /// <c>ext.key</c>).</summary>
    public Task<(ViewResponse? Response, Finding? Error)> QueryAsync(
        string viewId, IReadOnlyDictionary<string, string?>? query = null, CancellationToken ct = default) =>
        host.QueryAsync(this, viewId, query ?? new Dictionary<string, string?>(), ct);
}
