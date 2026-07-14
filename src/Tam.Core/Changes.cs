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

/// <summary>A structured field conflict from the three-way merge.</summary>
public sealed record FieldConflict(
    string Field,
    object? OriginalValue,
    object? CurrentValue,
    object? SubmittedValue);

public static class ConcurrencyFindings
{
    public static readonly FindingFactory FieldConflict = Finding.Error("concurrency.field-conflict");
    public static readonly FindingFactory VersionConflict = Finding.Error("concurrency.version-conflict");
}
