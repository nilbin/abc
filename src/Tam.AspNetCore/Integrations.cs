using System.Linq.Expressions;
using System.Text.Json;

namespace Tam.AspNetCore;

/// <summary>
/// Integrations are bindings into operations (docs/10): external payload → typed mapping →
/// normal operation execution with idempotency. No direct table writes, ever. This is the
/// lean first slice: mapping + INT001 validation + idempotent runner (inbox/retries later).
/// </summary>
public sealed class IntegrationBuilder<TSource, TInput>
{
    private readonly List<(string Target, Func<TSource, object?> Map)> mappings = [];
    private Func<TSource, string>? idempotencyKey;

    public IntegrationBuilder<TSource, TInput> Map<TValue>(
        Expression<Func<TInput, TValue>> target, Func<TSource, object?> map)
    {
        var member = target.Body switch
        {
            MemberExpression m => m.Member.Name,
            UnaryExpression { Operand: MemberExpression m } => m.Member.Name,
            _ => throw new ArgumentException("Expected a simple member access.", nameof(target)),
        };
        mappings.Add((Naming.Camel(member), map));
        return this;
    }

    public IntegrationBuilder<TSource, TInput> IdempotencyKey(Func<TSource, string> key)
    {
        idempotencyKey = key;
        return this;
    }

    public IntegrationDefinition<TSource> Build(string id, TamModel model, string operationId)
    {
        var operation = model.Operations[operationId];
        if (operation.InputType != typeof(TInput))
            throw new InvalidOperationException($"INT003: integration '{id}' input type mismatch.");

        var mapped = mappings.Select(m => m.Target).ToHashSet();
        var missing = operation.InputFields
            .Where(f => f.Required && !mapped.Contains(f.WireName))
            .Select(f => f.WireName).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"INT001: integration '{id}' does not map required field(s) {string.Join(", ", missing)} of '{operationId}'.");
        }

        if (idempotencyKey is null)
            throw new InvalidOperationException($"INT004: integration '{id}' has no idempotency key.");

        return new IntegrationDefinition<TSource>(id, operationId, mappings, idempotencyKey);
    }
}

public sealed record IntegrationDefinition<TSource>(
    string Id,
    string OperationId,
    IReadOnlyList<(string Target, Func<TSource, object?> Map)> Mappings,
    Func<TSource, string> IdempotencyKey);

public sealed record IntegrationRowResult(
    string Key, string Status, IReadOnlyList<Finding> Findings);   // created | replayed | failed

public static class IntegrationRunner
{
    /// <summary>Each row executes the target operation through the full pipeline, keyed for replay safety.</summary>
    public static async Task<IReadOnlyList<IntegrationRowResult>> Run<TSource>(
        IntegrationDefinition<TSource> integration,
        IEnumerable<TSource> rows,
        OperationExecutor executor,
        OperationContext context,
        CancellationToken ct)
    {
        var results = new List<IntegrationRowResult>();
        foreach (var row in rows)
        {
            var key = $"{integration.Id}:{integration.IdempotencyKey(row)}";
            var input = integration.Mappings
                .Select(m => (m.Target, Value: m.Map(row)))
                .Where(m => m.Value is not null)
                .ToDictionary(m => m.Target, m => m.Value);
            var body = JsonSerializer.SerializeToElement(input, TamJson.Options);

            var rowContext = new OperationContext
            {
                Actor = context.Actor,
                TenantId = context.TenantId,
                Source = InvocationSource.Integration,
                Culture = context.Culture,
                IdempotencyKey = key,
                CorrelationId = context.CorrelationId,
                Services = context.Services,
            };

            var response = await executor.ExecuteAsync(integration.OperationId, body, rowContext, ct);
            var failed = response.Findings.Any(f => f.Severity == FindingSeverity.Error);
            var replayed = response.Findings.Any(f => f.Code == PipelineFindings.IdempotentReplay.Code);
            results.Add(new IntegrationRowResult(
                key,
                failed ? "failed" : replayed ? "replayed" : "created",
                response.Findings));
        }
        return results;
    }
}
