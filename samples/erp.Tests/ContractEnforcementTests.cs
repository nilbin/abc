using Erp;
using Erp.Features;
using Tam;
using Tam.AspNetCore;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// The operation contract is enforced for EVERY caller at submit, not merely previewed at resolve
/// (docs/40, Sol re-review). Two authority gaps this pins closed:
///   • a BLOCKING derivation finding stops submit even when the handler would have succeeded — the
///     derivation is the sole admissibility check (Finding 1), so the pipeline, not a duplicated
///     handler check, must enforce it;
///   • the selected FORM is part of idempotency identity (Finding 2): the same body + key submitted
///     directly and through a form are different effective requests and must not replay each other.
/// </summary>
public sealed class ContractEnforcementTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid customerId;
    private TestActor<ErpDbContext> actor = null!;

    public async Task InitializeAsync()
    {
        // The REAL model plus a probe derivation on orders.create — added through the Builder() seam
        // so nothing is faked: the probe rides the same RunDerivationsAsync path production uses.
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(
            ErpModel.Builder().AddDerivationHost(typeof(BlockingProbeDerivations)).Build());
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Kontraktkund"), new("Kontraktsgatan 1"), null, null);
            db.Customers.Add(customer);
            customerId = customer.Id.Value;
            return Task.CompletedTask;
        });
        actor = host.Actor("demo", "orders.create");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private object ServiceOrder(string description) => new
    {
        customerId,
        orderType = "service",
        workAddress = "Verkstadsgatan 1",
        description,
    };

    [Fact]
    public async Task A_blocking_derivation_finding_stops_submit_even_when_the_handler_would_succeed()
    {
        // The sentinel description makes the probe emit a blocking field error; the create handler
        // has no such check and would happily create the order. The pipeline must reject it — proof
        // that derived.Findings are authoritative at submit, not just shown at resolve.
        (await actor.ExecuteAsync("orders.create", ServiceOrder(BlockingProbeDerivations.Sentinel)))
            .ShouldFailWith("test.probe-rejected", onField: "description");

        // Control: the same operation with an ordinary description succeeds — the probe is inert and
        // nothing else blocks, so this isolates the derivation finding as the sole cause above.
        (await actor.ExecuteAsync("orders.create", ServiceOrder("an ordinary order")))
            .ShouldSucceed();
    }

    [Fact]
    public async Task The_form_binding_is_part_of_idempotency_identity()
    {
        var body = ServiceOrder("idempotent order");

        // Direct, key K: a fresh outcome, not a replay.
        var first = await actor.ExecuteAsync("orders.create", body, idempotencyKey: "K");
        first.ShouldSucceed();
        Assert.DoesNotContain(first.Findings, f => f.Code == "pipeline.idempotent-replay");

        // Direct again, SAME body + key: THIS is the same effective request → replayed.
        var replay = await actor.ExecuteAsync("orders.create", body, idempotencyKey: "K");
        replay.ShouldSucceed();
        Assert.Contains(replay.Findings, f => f.Code == "pipeline.idempotent-replay");

        // Through the create form, SAME body + key: a DIFFERENT effective contract, so it is an
        // INDEPENDENT idempotency record — a fresh outcome, never the direct call's replayed result.
        var viaForm = await actor.ExecuteThroughFormAsync(
            "web.orders.create", "orders.create", body, idempotencyKey: "K");
        viaForm.ShouldSucceed();
        Assert.DoesNotContain(viaForm.Findings, f => f.Code == "pipeline.idempotent-replay");
    }
}

/// <summary>Test-only derivation on orders.create: emits a blocking finding on a sentinel input the
/// create handler never inspects, so a green submit through it can only mean the pipeline enforced
/// the derivation's finding. Registered via ErpModel.Builder().AddDerivationHost in the test.</summary>
public static class BlockingProbeDerivations
{
    public const string Sentinel = "__probe_block__";

    public static readonly FindingFactory Rejected = Finding.Error("test.probe-rejected");

    [ServerDerivation("test.orders.create.block-probe")]
    public static DerivationResult BlockOnSentinel(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == Sentinel
            ? DerivationResult.FieldError(nameof(CreateOrder.Input.Description), Rejected)
            : DerivationResult.Empty;
}
