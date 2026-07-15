namespace Tam;

/// <summary>Ambient execution state every operation and derivation runs under.</summary>
public sealed class OperationContext
{
    public required Actor Actor { get; init; }

    public required TenantId TenantId { get; init; }

    public required InvocationSource Source { get; init; }

    public required string Culture { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    public required IServiceProvider Services { get; init; }
}

/// <summary>Wire envelope of every operation response (docs/09).</summary>
public sealed record OperationResponse(
    object? Output,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<object> Effects,
    long? NewVersion,
    string? AuditReference,
    IReadOnlyList<FieldConflict>? Conflicts = null);

public sealed record ViewResponse(
    IReadOnlyList<object> Rows,
    int Total,
    int Page,
    int PageSize);
