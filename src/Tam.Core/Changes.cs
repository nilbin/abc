namespace Tam;

/// <summary>
/// One field in a partial edit (docs/07-partial-edits.md, docs/40): its loaded <see cref="Original"/>
/// (the merge base — client-reported, treated as untrusted) and its proposed <see cref="Value"/>. Under
/// the complete-state contract the form submits EVERY initialized change field, so a present
/// <c>Change&lt;T&gt;</c> does NOT mean "touched": an untouched field carries <c>Original == Value</c>,
/// and the effective patch is derived from <c>Original != Value</c> (semantically) by TamMerge, not from
/// property presence. A genuinely cleared field is <c>Value == null</c> with a non-null Original; an
/// initialized-null field is <c>Original == Value == null</c> (a no-op). A null Original is a valid merge
/// base, not "missing" — JSON cannot distinguish an explicit null from an omitted property.
/// </summary>
public sealed record Change<T>(T? Original, T? Value);

/// <summary>A structured field conflict from the three-way merge (Original / Current / Value all
/// differ). <see cref="Reason"/> is "stale" — a genuine concurrent edit. (The former "original-missing"
/// inference was removed in round 9: under the complete-state contract a null Original is a valid merge
/// base, not evidence of an omitted property.)</summary>
public sealed record FieldConflict(
    string Field,
    object? OriginalValue,
    object? CurrentValue,
    object? SubmittedValue,
    string Reason = "stale");

public static class ConcurrencyFindings
{
    public static readonly FindingFactory FieldConflict = Finding.Error("concurrency.field-conflict");
    public static readonly FindingFactory VersionConflict = Finding.Error("concurrency.version-conflict");
}
