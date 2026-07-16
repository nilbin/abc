namespace Tam.Testing;

/// <summary>Thrown by the Should* assertions — runner-agnostic (xUnit, NUnit, MSTest all
/// surface it as a failure with the message intact).</summary>
public sealed class TamAssertionException(string message) : Exception(message);

/// <summary>Fluent assertions over the pipeline's real envelopes (tutorial Step 11).</summary>
public static class Assertions
{
    /// <summary>No error findings and no conflicts. Returns the response for chaining.</summary>
    public static OperationResponse ShouldSucceed(this OperationResponse response)
    {
        var errors = response.Findings.Where(f => f.Severity == FindingSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new TamAssertionException(
                $"expected success but got: {string.Join("; ", errors.Select(Describe))}");
        if (response.Conflicts is { Count: > 0 } conflicts)
            throw new TamAssertionException(
                $"expected success but got field conflicts on: {string.Join(", ", conflicts.Select(c => c.Field))}");
        return response;
    }

    /// <summary>An error finding with exactly this code (e.g. "orders.invalid-customer"),
    /// optionally targeting a field.</summary>
    public static OperationResponse ShouldFailWith(
        this OperationResponse response, string code, string? onField = null)
    {
        var hit = response.Findings.FirstOrDefault(
            f => f.Severity == FindingSeverity.Error && f.Code == code);
        if (hit is null)
            throw new TamAssertionException(
                $"expected failure '{code}' but findings were: "
                + (response.Findings.Count == 0 ? "(none — the call succeeded)"
                    : string.Join("; ", response.Findings.Select(Describe))));
        if (onField is not null && !hit.Targets.Any(t => t.Value == onField))
            throw new TamAssertionException(
                $"'{code}' raised, but on [{string.Join(", ", hit.Targets)}] — expected '{onField}'");
        return response;
    }

    /// <summary>The paired-atom / authorization boundary: the pipeline refused the call.</summary>
    public static OperationResponse ShouldBeDenied(this OperationResponse response) =>
        response.ShouldFailWith("pipeline.not-authorized");

    /// <summary>A three-way-merge conflict on this field (docs/07).</summary>
    public static OperationResponse ShouldConflictOn(this OperationResponse response, string field)
    {
        if (response.Conflicts is not { Count: > 0 } conflicts
            || !conflicts.Any(c => c.Field == field))
            throw new TamAssertionException(
                $"expected a conflict on '{field}' but conflicts were: "
                + (response.Conflicts is { Count: > 0 } cs
                    ? string.Join(", ", cs.Select(c => c.Field)) : "(none)"));
        return response;
    }

    /// <summary>An effect of this shape rode the envelope — e.g. the published event a
    /// subscriber or integration trigger binds to (docs/31 D-X5).</summary>
    public static OperationResponse ShouldHaveEffect(
        this OperationResponse response, Func<object, bool> predicate, string? description = null)
    {
        if (!response.Effects.Any(predicate))
            throw new TamAssertionException(
                $"expected effect{(description is null ? "" : $" ({description})")} "
                + $"but effects were: [{string.Join("; ", response.Effects.Select(e => e.ToString()))}]");
        return response;
    }

    /// <summary>Shorthand for the common case: an <c>event-published</c> effect carrying this
    /// event type.</summary>
    public static OperationResponse ShouldPublish(this OperationResponse response, string eventType) =>
        response.ShouldHaveEffect(
            e => e.ToString()?.Contains(eventType, StringComparison.Ordinal) == true,
            $"event '{eventType}'");

    /// <summary>The typed output of a successful operation.</summary>
    public static T Output<T>(this OperationResponse response)
    {
        response.ShouldSucceed();
        return response.Output is T typed
            ? typed
            : throw new TamAssertionException(
                $"output is {response.Output?.GetType().Name ?? "null"}, not {typeof(T).Name}");
    }

    /// <summary>View twin of ShouldSucceed: the query executed (translation included) and
    /// returned rows.</summary>
    public static ViewResponse ShouldSucceed(this (ViewResponse? Response, Finding? Error) result)
    {
        if (result.Error is { } error)
            throw new TamAssertionException($"view failed: {Describe(error)}");
        return result.Response!;
    }

    public static void ShouldBeDenied(this (ViewResponse? Response, Finding? Error) result)
    {
        if (result.Error?.Code != "pipeline.not-authorized")
            throw new TamAssertionException(result.Error is { } e
                ? $"expected pipeline.not-authorized but got: {Describe(e)}"
                : $"expected denial but the view returned {result.Response!.Total} rows");
    }

    private static string Describe(Finding f) =>
        f.Targets.Count > 0 ? $"{f.Code} @ [{string.Join(", ", f.Targets)}]" : f.Code;
}
