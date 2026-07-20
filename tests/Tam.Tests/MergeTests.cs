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
}
