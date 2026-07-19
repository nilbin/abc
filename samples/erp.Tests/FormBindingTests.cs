using Erp;
using Erp.Features;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// Operations own their input contract; forms are projections that may TIGHTEN but never weaken it
/// (docs/40, Sol re-review Finding 2). Two rules make the distinction concrete:
///   • project-required is the create-order OPERATION's rule (a Require() derivation) — enforced for
///     every caller, whether or not a form is named;
///   • the rules.define form marks messages required when there's no action — a presentation
///     TIGHTENING that applies ONLY when that form is the one submitted; a direct call falls to the
///     operation's own domain rule (RUL003).
/// </summary>
public sealed class FormBindingTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid customerId;
    private TestActor<ErpDbContext> actor = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Testkund AB"), new("Testgatan 1"), null, null);
            db.Customers.Add(customer);
            customerId = customer.Id.Value;
            return Task.CompletedTask;
        });
        actor = host.Actor("demo", "orders.create", "rules.manage");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private object ProjectOrderMissingProject() => new
    {
        customerId,
        orderType = "project",
        workAddress = "Verkstadsgatan 1",
        description = "project order, no project chosen",
    };

    private static object FindingRuleMissingMessage() => new
    {
        name = "silent-finding",
        onOperation = "orders.complete",
        condition = """{"t":"const","v":true}""",
    };

    [Fact]
    public async Task Operation_owned_requiredness_governs_every_caller()
    {
        // Direct: no form binding, yet the operation's own Require() rule catches the missing
        // project with the precise domain finding.
        (await actor.ExecuteAsync("orders.create", ProjectOrderMissingProject()))
            .ShouldFailWith("orders.project-required");

        // Through the create form: SAME finding — the requiredness belongs to the operation, so the
        // form does not (and need not) restate it. It is not a generic validation.required.
        (await actor.ExecuteThroughFormAsync("web.orders.create", "orders.create", ProjectOrderMissingProject()))
            .ShouldFailWith("orders.project-required");
    }

    [Fact]
    public async Task Form_specific_tightening_applies_only_through_that_form()
    {
        // Through web.rules.define, the form's RequiredWhen(Action == null) tightening fires first,
        // as validation.required on messages — the form-specific rule, applied because THIS form
        // was named.
        (await actor.ExecuteThroughFormAsync("web.rules.define", "rules.define", FindingRuleMissingMessage()))
            .ShouldFailWith("validation.required", onField: "messages");

        // A direct call (MCP, integration, a script) is NOT bound by that form rule — it falls to
        // the operation's OWN domain rule (RUL003), which is richer (it wants the default culture).
        (await actor.ExecuteAsync("rules.define", FindingRuleMissingMessage()))
            .ShouldFailWith("rules.missing-message", onField: "messages");
    }

    [Fact]
    public async Task A_form_bound_to_another_operation_is_rejected()
    {
        // web.orders.schedule binds a DIFFERENT operation. Supplying it on an orders.create call is
        // not "apply it if it happens to fit, otherwise pretend no form was named" — once a binding
        // is supplied it is authoritative, so a mismatched one fails CLOSED (Sol re-review, Finding
        // 3), rather than silently downgrading to the direct contract.
        (await actor.ExecuteThroughFormAsync("web.orders.schedule", "orders.create", ProjectOrderMissingProject()))
            .ShouldFailWith("pipeline.invalid-input", onField: null);
    }

    [Fact]
    public async Task An_unknown_form_id_is_rejected()
    {
        // A typo'd form id must not slip through as a direct call — the caller asked to submit
        // through a specific binding that does not exist (Sol re-review, Finding 3).
        (await actor.ExecuteThroughFormAsync("web.orders.creat", "orders.create", ProjectOrderMissingProject()))
            .ShouldFailWith("pipeline.unknown-form");
    }
}
