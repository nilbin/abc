using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// Operations own their input contract; forms are projections that may TIGHTEN but never weaken it
/// (docs/40) — FRAMEWORK behavior. Two rules make the distinction concrete: bin-required is the
/// widgets.create OPERATION's rule (a Require() derivation), enforced for every caller whether or not a
/// form is named; the rules.define form marks messages required when there's no action — a presentation
/// TIGHTENING that applies ONLY when that form is the one submitted.
/// </summary>
public sealed class FormBindingTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;
    private TestActor<WidgetDbContext> actor = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());
        actor = host.Actor("demo", "widgets.create", "bins.read", "rules.manage");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private static object SpecialWidgetMissingBin() => new
    {
        name = "W", category = "special", groupId = Guid.NewGuid(), description = "special widget, no bin chosen",
    };

    private static object FindingRuleMissingMessage() => new
    {
        name = "silent-finding",
        onOperation = "widgets.complete",
        condition = """{"t":"const","v":true}""",
    };

    [Fact]
    public async Task Operation_owned_requiredness_governs_every_caller()
    {
        // Direct: no form binding, yet the operation's own Require() rule catches the missing bin with
        // the precise domain finding.
        (await actor.ExecuteAsync("widgets.create", SpecialWidgetMissingBin()))
            .ShouldFailWith("widgets.bin-required");

        // Through the create form: SAME finding — the requiredness belongs to the operation, so the form
        // does not (and need not) restate it. It is not a generic validation.required.
        (await actor.ExecuteThroughFormAsync("web.widgets.create", "widgets.create", SpecialWidgetMissingBin()))
            .ShouldFailWith("widgets.bin-required");
    }

    [Fact]
    public async Task Form_specific_tightening_applies_only_through_that_form()
    {
        // Through web.rules.define, the form's RequiredWhen(Action == null) tightening fires first, as
        // validation.required on messages — the form-specific rule, applied because THIS form was named.
        (await actor.ExecuteThroughFormAsync("web.rules.define", "rules.define", FindingRuleMissingMessage()))
            .ShouldFailWith("validation.required", onField: "messages");

        // A direct call (MCP, integration, a script) is NOT bound by that form rule — it falls to the
        // operation's OWN domain rule (RUL003), which is richer (it wants the default culture).
        (await actor.ExecuteAsync("rules.define", FindingRuleMissingMessage()))
            .ShouldFailWith("rules.missing-message", onField: "messages");
    }

    [Fact]
    public async Task A_form_bound_to_another_operation_is_rejected()
    {
        // web.widgets.edit binds a DIFFERENT operation. Supplying it on a widgets.create call fails
        // CLOSED once the binding is authoritative, rather than silently downgrading to the direct
        // contract.
        (await actor.ExecuteThroughFormAsync("web.widgets.edit", "widgets.create", SpecialWidgetMissingBin()))
            .ShouldFailWith("pipeline.invalid-input", onField: null);
    }

    [Fact]
    public async Task An_unknown_form_id_is_rejected()
    {
        // A typo'd form id must not slip through as a direct call — the caller asked to submit through a
        // specific binding that does not exist.
        (await actor.ExecuteThroughFormAsync("web.widgets.creat", "widgets.create", SpecialWidgetMissingBin()))
            .ShouldFailWith("pipeline.unknown-form");
    }
}
