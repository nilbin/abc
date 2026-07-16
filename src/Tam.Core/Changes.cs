namespace Tam;

/// <summary>
/// A user-touched field in a partial edit (docs/07-partial-edits.md).
/// Absent property = untouched. Present with <see cref="Value"/> null = explicit clear.
/// <see cref="Original"/> is the value the form loaded (the merge base) — client-reported, treated as untrusted.
/// </summary>
public sealed record Change<T>(T? Original, T? Value)
{
    public static Change<T> To(T? value) => new(default, value);
}

/// <summary>A structured field conflict from the three-way merge. <see cref="Reason"/>
/// distinguishes a GENUINE concurrent edit ("stale") from a raw-wire caller that omitted the
/// merge base entirely ("original-missing") — the two need different fixes (docs/34 M5).</summary>
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
