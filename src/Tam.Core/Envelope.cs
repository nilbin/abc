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

public static class PipelineFindings
{
    public static readonly FindingFactory NotAuthorized = Finding.Error("pipeline.not-authorized");
    public static readonly FindingFactory UnknownOperation = Finding.Error("pipeline.unknown-operation");
    public static readonly FindingFactory UnknownView = Finding.Error("pipeline.unknown-view");
    public static readonly FindingFactory UnknownForm = Finding.Error("pipeline.unknown-form");
    public static readonly FindingFactory InvalidInput = Finding.Error("pipeline.invalid-input");
    public static readonly FindingFactory NotFound = Finding.Error("pipeline.not-found");
    public static readonly FindingFactory IdempotentReplay = Finding.Information("pipeline.idempotent-replay");
}
