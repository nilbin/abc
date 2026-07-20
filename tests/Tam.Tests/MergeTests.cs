using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

public class MergeTests
{
    private sealed class Doc
    {
        public string? Description { get; private set; } = "Repair pump";
        public DateOnly? RequestedDate { get; private set; } = new(2026, 7, 20);
    }

    private sealed record EditInput(
        Change<string?>? Description = null,
        Change<DateOnly?>? RequestedDate = null);

    [Fact]
    public void Non_overlapping_stale_edit_merges_cleanly()
    {
        var doc = new Doc();
        // Current Description already moved to "Replace pump" by user B:
        TamMerge.Apply(doc, new EditInput(Description: new("Repair pump", "Replace pump")));

        // User A, holding the old base, edits a DIFFERENT field:
        var merge = TamMerge.Apply(doc, new EditInput(
            RequestedDate: new(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 22))));

        Assert.False(merge.HasConflicts);
        Assert.Equal(["requestedDate"], merge.AppliedFields);
        Assert.Equal(new DateOnly(2026, 7, 22), doc.RequestedDate);
        Assert.Equal("Replace pump", doc.Description);
    }

    [Fact]
    public void Same_field_stale_edit_is_a_structured_conflict()
    {
        var doc = new Doc();
        TamMerge.Apply(doc, new EditInput(Description: new("Repair pump", "Replace pump")));

        var merge = TamMerge.Apply(doc, new EditInput(
            Description: new("Repair pump", "Inspect pump")));

        var conflict = Assert.Single(merge.Conflicts);
        Assert.Equal("description", conflict.Field);
        Assert.Equal("Repair pump", conflict.OriginalValue);
        Assert.Equal("Replace pump", conflict.CurrentValue);
        Assert.Equal("Inspect pump", conflict.SubmittedValue);
        Assert.Equal("Replace pump", doc.Description);   // nothing applied
    }

    [Fact]
    public void Submitting_the_current_value_is_already_resolved_not_a_conflict()
    {
        var doc = new Doc();
        TamMerge.Apply(doc, new EditInput(Description: new("Repair pump", "Replace pump")));

        var merge = TamMerge.Apply(doc, new EditInput(
            Description: new("Repair pump", "Replace pump")));

        Assert.False(merge.HasConflicts);
        Assert.Empty(merge.AppliedFields);
    }

    [Fact]
    public void Explicit_clear_applies_null()
    {
        var doc = new Doc();
        var merge = TamMerge.Apply(doc, new EditInput(
            RequestedDate: new(new DateOnly(2026, 7, 20), null)));

        Assert.False(merge.HasConflicts);
        Assert.Null(doc.RequestedDate);
    }

    [Fact]
    public void Unchanged_field_with_a_concurrent_database_change_is_a_no_op()
    {
        var doc = new Doc();   // Description = "Repair pump"
        // User B moves Description on (current now differs from the merge base):
        TamMerge.Apply(doc, new EditInput(Description: new("Repair pump", "Replace pump")));

        // User A, holding the OLD base, submits Description UNCHANGED (Original == Value) — the
        // complete-submission shape for a field never touched (docs/40, round 8). No write, no conflict,
        // and B's concurrent value stands: an untouched field takes no concurrency check. Before the
        // Original == Value branch this would have raised a spurious conflict.
        var merge = TamMerge.Apply(doc, new EditInput(Description: new("Repair pump", "Repair pump")));

        Assert.False(merge.HasConflicts);
        Assert.Empty(merge.AppliedFields);
        Assert.Equal("Replace pump", doc.Description);
    }
}

public class ExtensionApplierTests
{
    private sealed class Thing : IExtensible
    {
        public ExtensionData Extensions { get; set; } = new();
    }

    private static ExtensionFieldSpec Spec(
        string key = "machineSerialNumber",
        ExtensionFieldState state = ExtensionFieldState.Active,
        bool required = false,
        int? maxLength = 40) => new(
        key, "order", "text", required, maxLength,
        new Dictionary<string, string> { ["sv"] = "Maskinserienummer" },
        null, null, state);

    private static Dictionary<string, ExtensionChange> Changes(string key, string? original, string? value) => new()
    {
        [key] = new ExtensionChange(
            original is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(original),
            value is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(value)),
    };

    [Fact]
    public void Valid_change_applies_and_is_readable()
    {
        var thing = new Thing();
        var result = ExtensionApplier.Apply(thing, true, Changes("machineSerialNumber", null, "MX-1"), [Spec()]);

        Assert.Empty(result.Findings);
        Assert.Equal("MX-1", thing.Extensions.Get<string>("machineSerialNumber"));
    }

    [Fact]
    public void Unknown_and_retired_fields_are_rejected_with_findings()
    {
        var thing = new Thing();
        var unknown = ExtensionApplier.Apply(thing, true, Changes("nope", null, "x"), [Spec()]);
        Assert.Contains(unknown.Findings, f => f.Code == "extensions.unknown-field");

        var retired = ExtensionApplier.Apply(thing, true,
            Changes("machineSerialNumber", null, "x"), [Spec(state: ExtensionFieldState.Retired)]);
        Assert.Contains(retired.Findings, f => f.Code == "extensions.retired-field");
    }

    [Fact]
    public void Constraint_violation_is_an_ordinary_finding()
    {
        var thing = new Thing();
        var result = ExtensionApplier.Apply(thing, true,
            Changes("machineSerialNumber", null, new string('x', 60)), [Spec(maxLength: 40)]);
        Assert.Contains(result.Findings, f => f.Code == "validation.too-long");
    }

    [Fact]
    public void Concurrent_extension_edit_conflicts_three_way()
    {
        var thing = new Thing();
        ExtensionApplier.Apply(thing, true, Changes("machineSerialNumber", null, "CURRENT"), [Spec()]);

        var stale = ExtensionApplier.Apply(thing, false,
            Changes("machineSerialNumber", "OLD-BASE", "MINE"), [Spec()]);

        var conflict = Assert.Single(stale.Conflicts);
        Assert.Equal("extensions.machineSerialNumber", conflict.Field);
        Assert.Equal("CURRENT", conflict.CurrentValue);
    }

    [Fact]
    public void EffectivePatch_drops_unchanged_extension_fields_before_target_selection()
    {
        // Complete-state submission sends every initialized extension field, including unchanged ones
        // on an edit (Sol re-review round 10, F2). EffectivePatch reduces to the real patch so the
        // pipeline skips extension-target selection entirely when nothing changed — no
        // ambiguous-extension-target from carrying unchanged values.
        var specs = new[] { Spec("machineSerialNumber") };
        var changes = new Dictionary<string, ExtensionChange>
        {
            ["machineSerialNumber"] = Changes("machineSerialNumber", "S1", "S1")["machineSerialNumber"],   // no-op
            ["other"] = Changes("other", "S2", "S3")["other"],                                             // changed (unknown key)
        };
        var effective = ExtensionApplier.EffectivePatch(changes, specs);

        Assert.False(effective.ContainsKey("machineSerialNumber"));   // Original == Value → dropped
        Assert.True(effective.ContainsKey("other"));                  // changed → retained (Apply reports unknown-field)

        // The all-unchanged case reduces to an empty patch — the pipeline then does no target lookup.
        var allNoOp = ExtensionApplier.EffectivePatch(
            Changes("machineSerialNumber", "S1", "S1"), specs);
        Assert.Empty(allNoOp);
    }

    [Fact]
    public void Unchanged_extension_with_a_concurrent_change_is_a_no_op()
    {
        var thing = new Thing();
        ExtensionApplier.Apply(thing, true, Changes("machineSerialNumber", null, "CURRENT"), [Spec()]);

        // Extensions share the main-field concurrency model (round 8): an untouched extension change
        // (Original == Value) submitted alongside the rest is a no-op, never a conflict, even though the
        // DB moved on.
        var merge = ExtensionApplier.Apply(thing, false,
            Changes("machineSerialNumber", "OLD-BASE", "OLD-BASE"), [Spec()]);

        Assert.Empty(merge.Conflicts);
        Assert.Empty(merge.Findings);
        Assert.Equal("CURRENT", thing.Extensions.Get<string>("machineSerialNumber"));
    }

    [Fact]
    public void Required_active_fields_are_enforced_on_new_entities()
    {
        var thing = new Thing();
        var result = ExtensionApplier.Apply(thing, true,
            new Dictionary<string, ExtensionChange>(), [Spec(required: true)]);
        Assert.Contains(result.Findings, f => f.Code == "validation.required");
    }

    [Fact]
    public void A_new_entity_applies_a_prefilled_value_that_looks_like_an_edit_no_op()
    {
        // On a CREATE the submitted Value IS the initial state, even when it arrives as {original: X,
        // value: X} — a prefilled create form freezes the same value as baseline and current (Sol
        // re-review round 12, F2). Apply's Original == Value skip is edit-only, so the value persists
        // rather than silently vanishing.
        var thing = new Thing();
        var result = ExtensionApplier.Apply(thing, true,
            Changes("machineSerialNumber", "MX-9", "MX-9"), [Spec()]);
        Assert.Empty(result.Findings);
        Assert.Equal("MX-9", thing.Extensions.Get<string>("machineSerialNumber"));
    }

    [Fact]
    public void An_edit_no_op_still_writes_nothing()
    {
        // The twin of the case above: on an EDIT the same {original: X, value: X} is a genuine no-op.
        var thing = new Thing();
        ExtensionApplier.Apply(thing, true, Changes("machineSerialNumber", null, "SEED"), [Spec()]);
        var result = ExtensionApplier.Apply(thing, false,
            Changes("machineSerialNumber", "SEED", "SEED"), [Spec()]);
        Assert.Empty(result.Findings);
        Assert.Empty(result.AppliedKeys);
        Assert.Equal("SEED", thing.Extensions.Get<string>("machineSerialNumber"));
    }
}

public class ExtensionWritePlanTests
{
    private static ExtensionFieldSpec RequiredSpec => new(
        "warrantyRef", "order", "text", true, null,
        new Dictionary<string, string> { ["sv"] = "Garanti" }, null, null, ExtensionFieldState.Active);

    // PlanWrite is pure over EF states (Sol re-review round 12, F3) — no DbContext needed to prove the
    // targeting + fail-closed policy that the pipeline delegates to.

    [Fact]
    public void Single_new_target_with_a_required_field_and_no_patch_still_validates()
    {
        var plan = ExtensionApplier.PlanWrite([EntityState.Added], hasRequiredActive: true, changeCount: 0, effectiveCount: 0);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.Apply, plan.Kind);
        Assert.Equal(0, plan.TargetIndex);
        Assert.True(plan.IsNewTarget);
    }

    [Fact]
    public void Two_new_targets_with_a_required_field_fail_closed_rather_than_skip_validation()
    {
        // The round-12 F3 hole: two Added extensible rows + required field + no patch previously produced
        // no target, no ambiguity and NO required validation — a silent bypass. Now it fails closed.
        var plan = ExtensionApplier.PlanWrite(
            [EntityState.Added, EntityState.Added], hasRequiredActive: true, changeCount: 0, effectiveCount: 0);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.Ambiguous, plan.Kind);
    }

    [Fact]
    public void One_new_plus_a_modified_sibling_with_a_required_field_fails_closed()
    {
        var plan = ExtensionApplier.PlanWrite(
            [EntityState.Added, EntityState.Modified], hasRequiredActive: true, changeCount: 0, effectiveCount: 0);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.Ambiguous, plan.Kind);
    }

    [Fact]
    public void An_edit_with_no_effective_patch_does_no_target_lookup()
    {
        // A lone Modified row carrying only unchanged extensions (effective empty), no required-create
        // concern — the pipeline must skip entirely (round 10, F2 preserved). A stray changeCount does not
        // count as edit work — only the effective patch does.
        var plan = ExtensionApplier.PlanWrite([EntityState.Modified], hasRequiredActive: false, changeCount: 3, effectiveCount: 0);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.None, plan.Kind);
    }

    [Fact]
    public void A_new_target_runs_on_a_prefilled_no_op_patch()
    {
        // effectiveCount 0 (all {X,X}) but changeCount 1: a create must still run so the prefilled value
        // is applied (round 12, F2). Work for a new target is the COMPLETE submitted count.
        var plan = ExtensionApplier.PlanWrite([EntityState.Added], hasRequiredActive: false, changeCount: 1, effectiveCount: 0);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.Apply, plan.Kind);
        Assert.True(plan.IsNewTarget);
        Assert.Equal(0, plan.TargetIndex);
    }

    [Fact]
    public void A_new_target_among_loaded_unchanged_siblings_is_still_selected()
    {
        // A create that also loaded sibling rows (Unchanged) — the single written (Added) one is the
        // target; loaded-unchanged rows do not make it ambiguous.
        var plan = ExtensionApplier.PlanWrite(
            [EntityState.Unchanged, EntityState.Added, EntityState.Unchanged],
            hasRequiredActive: true, changeCount: 0, effectiveCount: 0);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.Apply, plan.Kind);
        Assert.Equal(1, plan.TargetIndex);
        Assert.True(plan.IsNewTarget);
    }

    [Fact]
    public void A_real_edit_patch_with_no_tracked_target_fails_closed_as_not_found()
    {
        // Round 13, F1: a real effective patch (effectiveCount 1) but ZERO tracked extensible rows — an
        // untracked/direct-update handler, or an operation wrongly declared extensible. Previously the
        // planner said "run, not ambiguous, no target" and the executor silently dropped the patch. Now it
        // fails closed as target-not-found.
        var plan = ExtensionApplier.PlanWrite([], hasRequiredActive: false, changeCount: 1, effectiveCount: 1);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.TargetNotFound, plan.Kind);
    }

    [Fact]
    public void Two_new_targets_carrying_only_optional_prefill_fail_closed()
    {
        // Round 13, F2: two Added rows, no required spec, an OPTIONAL prefilled {X,X} (changeCount 1,
        // effectiveCount 0). Deciding work from a selected target was circular — no unique target meant
        // work read from effectiveCount (0) and the prefill silently vanished. Create work now comes from
        // the new candidates, so this is ambiguous, not a no-op.
        var plan = ExtensionApplier.PlanWrite(
            [EntityState.Added, EntityState.Added], hasRequiredActive: false, changeCount: 1, effectiveCount: 0);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.Ambiguous, plan.Kind);
    }

    [Fact]
    public void One_new_plus_a_modified_sibling_carrying_only_optional_prefill_fails_closed()
    {
        var plan = ExtensionApplier.PlanWrite(
            [EntityState.Added, EntityState.Modified], hasRequiredActive: false, changeCount: 1, effectiveCount: 0);
        Assert.Equal(ExtensionApplier.ExtensionPlanKind.Ambiguous, plan.Kind);
    }
}
