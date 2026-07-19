using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task An_unknown_lookup_base_filter_fails_closed()
    {
        // The probe binds ProjectId to projects.lookup with a TYPO'd base filter ("custmerId"). A
        // silently-ignored key would widen the candidate universe to "any project with this id" and
        // admit an out-of-scope selection — so membership refuses to run and the contract bug is
        // surfaced loudly (Sol re-review, Finding 5), rather than accepting the value.
        // A SERVICE order, so the real AvailableProjects lookup (which would short-circuit on the
        // unknown project first) does not fire — the probe's bad-filter lookup is the one exercised.
        var input = new
        {
            customerId,
            orderType = "service",
            projectId = Guid.NewGuid(),
            workAddress = "Verkstadsgatan 9",
            description = BlockingProbeDerivations.BadFilterSentinel,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => actor.ExecuteAsync("orders.create", input));
    }

    [Fact]
    public async Task A_closed_inline_option_set_is_enforced_at_submit()
    {
        // The probe declares WorkAddress's complete legal set. A value outside it is rejected...
        (await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Otillåtnagatan 99",
            description = BlockingProbeDerivations.ClosedOptionsSentinel,
        })).ShouldFailWith("orders.project-not-available", onField: "workAddress");

        // ...and a value inside it (matched through the option's semantic wrapper) goes through.
        (await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = BlockingProbeDerivations.AllowedAddress,
            description = BlockingProbeDerivations.ClosedOptionsSentinel,
        })).ShouldSucceed();

        // A case-variant is NOT accepted — the comparison is ordinal, not case-folding, so a
        // case-sensitive code/reference can't slip through in the wrong casing (Sol re-review F5).
        (await actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = BlockingProbeDerivations.AllowedAddress.ToLowerInvariant(),
            description = BlockingProbeDerivations.ClosedOptionsSentinel,
        })).ShouldFailWith("orders.project-not-available", onField: "workAddress");
    }

    [Fact]
    public async Task An_unsupported_lookup_filter_operator_fails_closed()
    {
        // customerId is a Guid, so `.contains` generates no predicate — accepting it would widen the
        // universe just like an unknown key. Now rejected as a contract bug (Sol re-review, Finding 2).
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            projectId = Guid.NewGuid(),
            workAddress = "Verkstadsgatan 10",
            description = BlockingProbeDerivations.BadOperatorSentinel,
        }));
    }

    [Fact]
    public async Task Two_lookup_bindings_for_one_field_fail_closed()
    {
        // Resolve would show one candidate universe while submit enforces both — the UI can't
        // represent that, so the contract is refused (Sol re-review, Finding 6).
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            projectId = Guid.NewGuid(),
            workAddress = "Verkstadsgatan 11",
            description = BlockingProbeDerivations.DoubleLookupSentinel,
        }));
    }

    [Fact]
    public async Task A_derivation_that_mutates_tracked_state_fails_closed()
    {
        // The probe adds an entity to the operation's writable context. Derivations must be read-only,
        // so the pipeline detects the tracked write and refuses (Sol re-review, Finding 3) — the
        // smuggled Customer is never committed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.ExecuteAsync("orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "Verkstadsgatan 12",
            description = BlockingProbeDerivations.MutateSentinel,
        }));

        var smuggled = await host.QueryDbAsync("demo", db =>
            db.Customers.CountAsync(c => c.Name == new CustomerName("Smuggelkund")));
        Assert.Equal(0, smuggled);
    }
}

/// <summary>Test-only derivation on orders.create: emits a blocking finding on a sentinel input the
/// create handler never inspects, so a green submit through it can only mean the pipeline enforced
/// the derivation's finding. Registered via ErpModel.Builder().AddDerivationHost in the test.</summary>
public static class BlockingProbeDerivations
{
    public const string Sentinel = "__probe_block__";
    public const string BadFilterSentinel = "__bad_filter__";
    public const string BadOperatorSentinel = "__bad_operator__";
    public const string ClosedOptionsSentinel = "__closed_options__";
    public const string DoubleLookupSentinel = "__double_lookup__";
    public const string MutateSentinel = "__mutate__";
    public const string AllowedAddress = "Tillåtnagatan 1";

    public static readonly FindingFactory Rejected = Finding.Error("test.probe-rejected");

    [ServerDerivation("test.orders.create.block-probe")]
    public static DerivationResult BlockOnSentinel(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == Sentinel
            ? DerivationResult.FieldError(nameof(CreateOrder.Input.Description), Rejected)
            : DerivationResult.Empty;

    // Binds ProjectId to a lookup whose base filter key is misspelled — only under the sentinel, so
    // ordinary orders in this host are untouched. Proves membership fails closed on a bogus filter.
    [ServerDerivation("test.orders.create.bad-filter-probe")]
    public static DerivationResult BadFilter(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == BadFilterSentinel
            ? DerivationResult.Empty.Lookup(
                nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                new Dictionary<string, string?> { ["custmerId"] = input.CustomerId.Value.ToString() },
                OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;

    // Binds ProjectId with an UNSUPPORTED operator for the field type: customerId on projects.lookup
    // is a Guid, so `.contains` is not a legal predicate. A silently-dropped operator would widen the
    // universe exactly like an unknown key (Sol re-review, Finding 2) — so it must fail closed too.
    [ServerDerivation("test.orders.create.bad-operator-probe")]
    public static DerivationResult BadOperator(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == BadOperatorSentinel
            ? DerivationResult.Empty.Lookup(
                nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                new Dictionary<string, string?> { ["customerId.contains"] = input.CustomerId.Value.ToString() },
                OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;

    // Declares an authoritative CLOSED option set on WorkAddress — the complete legal set — under the
    // sentinel. The option value is a SEMANTIC WRAPPER (Address), which membership must unwrap to its
    // scalar to compare against the submitted key (Sol re-review, Finding 5).
    [ServerDerivation("test.orders.create.closed-options-probe")]
    public static DerivationResult ClosedOptions(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == ClosedOptionsSentinel
            ? DerivationResult.Empty.RequireOneOf(
                nameof(CreateOrder.Input.WorkAddress),
                [new Option(new Address(AllowedAddress), AllowedAddress)],
                OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;

    // Emits TWO lookup bindings for the SAME field in one evaluation — resolve would show one and
    // submit would enforce both (Sol re-review, Finding 6). Must fail closed (DER008).
    [ServerDerivation("test.orders.create.double-lookup-probe")]
    public static DerivationResult DoubleLookup(CreateOrder.Input input, DerivationContext context) =>
        input.Description.Value == DoubleLookupSentinel
            ? DerivationResult.Empty
                .Lookup(nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                    new Dictionary<string, string?>(), OrderFindings.ProjectNotAvailable)
                .Lookup(nameof(CreateOrder.Input.ProjectId), "projects.lookup",
                    new Dictionary<string, string?>(), OrderFindings.ProjectNotAvailable)
            : DerivationResult.Empty;

    // MUTATES the operation's writable context — the exact thing derivations must not do (Sol
    // re-review, Finding 3). The pipeline must detect the tracked write and fail closed (DER007).
    [ServerDerivation("test.orders.create.mutate-probe")]
    public static DerivationResult Mutate(CreateOrder.Input input, DerivationContext context, ErpDbContext db)
    {
        if (input.Description.Value == MutateSentinel)
            db.Customers.Add(Customer.Create("demo", new("Smuggelkund"), new("Smuggelgatan 1"), null, null));
        return DerivationResult.Empty;
    }
}
