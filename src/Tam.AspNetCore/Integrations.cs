using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

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
    string Key, string Status, IReadOnlyList<Finding> Findings);   // created | replayed | failed | dead

public static class IntegrationRunner
{
    public const int MaxAttempts = 3;

    /// <summary>Cap on rows re-driven in a single inbound call: a large failed backlog drains a
    /// batch at a time across calls instead of reprocessing thousands inside one partner request.</summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Inbox-backed processing (docs/10): incoming rows are persisted first, then every
    /// pending/failed row for this integration — including ones from earlier runs — is
    /// (re)processed from its stored payload through the full operation pipeline. A row that
    /// keeps failing is dead-lettered after <see cref="MaxAttempts"/>; a fixed root cause
    /// recovers on the next run without the external system re-sending anything.
    /// </summary>
    public static async Task<IReadOnlyList<IntegrationRowResult>> Run<TSource>(
        IntegrationDefinition<TSource> integration,
        IEnumerable<TSource> rows,
        OperationExecutor executor,
        OperationContext context,
        Microsoft.EntityFrameworkCore.DbContext db,
        CancellationToken ct)
    {
        var inbox = db.Set<InboxRecord>();

        // 1. Receive: persist unseen rows before any processing.
        foreach (var row in rows)
        {
            var key = $"{integration.Id}:{integration.IdempotencyKey(row)}";
            var existing = inbox.Local.FirstOrDefault(x => x.Key == key)
                ?? await inbox.FirstOrDefaultAsync(
                    x => x.IntegrationId == integration.Id && x.Key == key, ct);
            if (existing is null)
            {
                inbox.Add(new InboxRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = context.TenantId.Value,
                    IntegrationId = integration.Id,
                    Key = key,
                    PayloadJson = JsonSerializer.Serialize(row, TamJson.Options),
                    ReceivedAt = DateTimeOffset.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync(ct);

        // 2. Process the due backlog, oldest first: never-tried Pending rows, plus Failed rows
        //    whose backoff has elapsed. Not-yet-due failures aren't even fetched, and the batch is
        //    bounded so a large failed backlog drains over several calls, not all in this request.
        var policy = RetryPolicy.Resolve(context.Services);
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var retryable = (await inbox
            .Where(x => x.IntegrationId == integration.Id
                && (x.Status == InboxStatus.Pending
                    || (x.Status == InboxStatus.Failed
                        && (x.NextAttemptIso == null || string.Compare(x.NextAttemptIso, nowIso) <= 0))))
            .ToListAsync(ct))
            .OrderBy(x => x.ReceivedAt)   // client-side: SQLite can't ORDER BY DateTimeOffset
            .Take(IntegrationRunner.BatchSize)
            .ToList();

        var results = new List<IntegrationRowResult>();
        foreach (var record in retryable)
        {
            var source = JsonSerializer.Deserialize<TSource>(record.PayloadJson, TamJson.Options)!;
            var input = integration.Mappings
                .Select(m => (m.Target, Value: m.Map(source)))
                .Where(m => m.Value is not null)
                .ToDictionary(m => m.Target, m => m.Value);
            var body = JsonSerializer.SerializeToElement(input, TamJson.Options);

            var rowContext = new OperationContext
            {
                Actor = context.Actor,
                TenantId = context.TenantId,
                Source = InvocationSource.Integration,
                Culture = context.Culture,
                IdempotencyKey = record.Key,
                CorrelationId = context.CorrelationId,
                Services = context.Services,
            };

            var response = await executor.ExecuteAsync(integration.OperationId, body, rowContext, ct);
            var failed = response.Findings.Any(f => f.Severity == FindingSeverity.Error);
            var replayed = response.Findings.Any(f => f.Code == PipelineFindings.IdempotentReplay.Code);

            if (failed)
            {
                record.Attempts++;
                record.Status = policy.IsExhausted(record.Attempts) ? InboxStatus.Dead : InboxStatus.Failed;
                record.LastError = string.Join(", ", response.Findings
                    .Where(f => f.Severity == FindingSeverity.Error).Select(f => f.Code).Distinct());
                record.NextAttemptIso = record.Status == InboxStatus.Failed
                    ? policy.NextAttempt(record.Attempts, DateTimeOffset.UtcNow).ToString("O")
                    : null;
            }
            else
            {
                record.Status = InboxStatus.Processed;
                record.ProcessedAt = DateTimeOffset.UtcNow;
                record.LastError = null;
                record.NextAttemptIso = null;
            }

            results.Add(new IntegrationRowResult(
                record.Key,
                record.Status == InboxStatus.Dead ? "dead"
                    : failed ? "failed"
                    : replayed ? "replayed" : "created",
                response.Findings));
        }
        await db.SaveChangesAsync(ct);

        return results;
    }
}

/// <summary>
/// Runs a plugin-shipped integration (docs/22): the handler maps the raw payload to wire rows,
/// which are persisted to the same inbox and processed through the target operation with the
/// identical idempotency/retry/dead-letter semantics as authored integrations. Wire-only —
/// the framework never sees the plugin's vendor types.
/// </summary>
public static class PluginIntegrationRunner
{
    public static async Task<IReadOnlyList<IntegrationRowResult>> RunAsync(
        PluginIntegrationDefinition integration,
        System.Text.Json.JsonElement payload,
        OperationExecutor executor,
        OperationContext context,
        Microsoft.EntityFrameworkCore.DbContext db,
        CancellationToken ct)
    {
        var inbox = db.Set<InboxRecord>();

        // 1. Receive: persist each unseen SOURCE row under its key. Storing the source (not the
        //    mapped input) is what lets step 2 re-map on retry and recover late-fixed rows.
        if (payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in payload.EnumerateArray())
            {
                var key = $"{integration.Id}:{integration.Key(element)}";
                var seen = inbox.Local.Any(x => x.Key == key)
                    || await inbox.AnyAsync(x => x.IntegrationId == integration.Id && x.Key == key, ct);
                if (!seen)
                {
                    inbox.Add(new InboxRecord
                    {
                        Id = Guid.NewGuid(),
                        TenantId = context.TenantId.Value,
                        IntegrationId = integration.Id,
                        Key = key,
                        PayloadJson = element.GetRawText(),
                        ReceivedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
            await db.SaveChangesAsync(ct);
        }

        // 2. Process the due backlog, oldest first — re-mapping each from its stored source. Same
        //    backoff gate and batch bound as the authored runner.
        var policy = RetryPolicy.Resolve(context.Services);
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var retryable = (await inbox
            .Where(x => x.IntegrationId == integration.Id
                && (x.Status == InboxStatus.Pending
                    || (x.Status == InboxStatus.Failed
                        && (x.NextAttemptIso == null || string.Compare(x.NextAttemptIso, nowIso) <= 0))))
            .ToListAsync(ct))
            .OrderBy(x => x.ReceivedAt)
            .Take(IntegrationRunner.BatchSize)
            .ToList();

        var results = new List<IntegrationRowResult>();
        foreach (var record in retryable)
        {
            var sourceRow = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(record.PayloadJson);
            var input = await integration.Map(sourceRow, context.Services, context, ct);
            var body = JsonSerializer.SerializeToElement(input, TamJson.Options);

            var rowContext = new OperationContext
            {
                Actor = context.Actor,
                TenantId = context.TenantId,
                Source = InvocationSource.Integration,
                Culture = context.Culture,
                IdempotencyKey = record.Key,
                CorrelationId = context.CorrelationId,
                Services = context.Services,
            };

            var response = await executor.ExecuteAsync(integration.OperationId, body, rowContext, ct);
            var failed = response.Findings.Any(f => f.Severity == FindingSeverity.Error);
            var replayed = response.Findings.Any(f => f.Code == PipelineFindings.IdempotentReplay.Code);

            if (failed)
            {
                record.Attempts++;
                record.Status = policy.IsExhausted(record.Attempts) ? InboxStatus.Dead : InboxStatus.Failed;
                record.LastError = string.Join(", ", response.Findings
                    .Where(f => f.Severity == FindingSeverity.Error).Select(f => f.Code).Distinct());
                record.NextAttemptIso = record.Status == InboxStatus.Failed
                    ? policy.NextAttempt(record.Attempts, DateTimeOffset.UtcNow).ToString("O")
                    : null;
            }
            else
            {
                record.Status = InboxStatus.Processed;
                record.ProcessedAt = DateTimeOffset.UtcNow;
                record.LastError = null;
                record.NextAttemptIso = null;
            }

            results.Add(new IntegrationRowResult(
                record.Key,
                record.Status == InboxStatus.Dead ? "dead" : failed ? "failed"
                    : replayed ? "replayed" : "created",
                response.Findings));
        }
        await db.SaveChangesAsync(ct);
        return results;
    }
}
