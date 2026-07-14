using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The outbound side of the retry story (docs/25): a failed event/schedule push is enqueued as an
/// <see cref="OutboundTaskEntity"/> so the <see cref="IntegrationRetryDriver"/> re-drives it with
/// backoff and dead-letters it after the shared cap — the outbound mirror of the inbound inbox.
/// Manual runs are excluded: those are user-initiated and synchronous, their result is the answer.
/// </summary>
public static class OutboundRetryQueue
{
    /// <summary>
    /// Records a failed event/schedule run for background retry. Event failures each get their own
    /// task (the stored payload replays the exact push); schedule failures dedupe to one live task
    /// per (tenant, integration) since the schedule's own cadence already re-drives it.
    /// Does not save — the caller commits it alongside the run record in one transaction.
    /// </summary>
    public static async Task EnqueueFailureAsync(
        DbContext db, RetryPolicy policy, string tenantId, string integrationId,
        string trigger, string? payloadJson, string? error, CancellationToken ct)
    {
        if (trigger == "schedule")
        {
            var live = await db.Set<OutboundTaskEntity>().AnyAsync(x =>
                x.TenantId == tenantId && x.IntegrationId == integrationId
                && x.Trigger == "schedule" && x.Status == InboxStatus.Failed, ct);
            if (live) return;
        }

        var now = DateTimeOffset.UtcNow;
        db.Add(new OutboundTaskEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IntegrationId = integrationId,
            Trigger = trigger,
            PayloadJson = payloadJson,
            Attempts = 1,                       // the inline run that just failed is attempt 1
            Status = InboxStatus.Failed,
            LastError = error,
            NextAttemptIso = policy.NextAttempt(1, now).ToString("O"),
            CreatedAtIso = now.ToString("O"),
        });
    }
}

/// <summary>
/// Drains the outbound retry queue (docs/25): each tick claims due tasks under the NextAttemptIso
/// lease (so instances don't double-run), re-invokes the handler with a per-run timeout, and rolls
/// the task forward — success closes it, a fresh failure backs off, and the cap dead-letters it.
/// The inbound inbox drains on the next inbound call (it must run under the caller's actor); this
/// driver is the outbound half, running under the same SystemContext the scheduler uses.
/// </summary>
public sealed class IntegrationRetryDriver(
    IServiceScopeFactory scopes,
    Func<IServiceProvider, DbContext> dbResolver,
    TamModel model,
    RetryPolicy policy,
    TimeSpan interval) : BackgroundService
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { /* a bad tick must not kill the loop */ }
            await Task.Delay(interval, ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = dbResolver(scope.ServiceProvider);
        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");

        var due = (await db.Set<OutboundTaskEntity>()
                .Where(x => x.Status == InboxStatus.Failed && string.Compare(x.NextAttemptIso, nowIso) <= 0)
                .OrderBy(x => x.NextAttemptIso)
                .Take(BatchSize)
                .ToListAsync(ct))
            .Where(x => DateTimeOffset.TryParse(x.NextAttemptIso, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var n) && n <= now)
            .ToList();

        // One activation lookup per tenant per tick, not per task (review-round-3 N+1).
        var activations = new Dictionary<string, IReadOnlySet<string>>();

        foreach (var task in due)
        {
            if (!model.OutboundIntegrations.TryGetValue(task.IntegrationId, out var integration))
                continue;
            if (!activations.TryGetValue(task.TenantId, out var active))
                activations[task.TenantId] = active = await PluginActivations.ActiveAsync(db, task.TenantId, ct);
            if (!active.Contains(integration.PluginId)) continue;

            // Claim under the lease before running: push NextAttemptIso forward for the attempt we
            // are about to make. A racing instance collides on the token and skips.
            task.NextAttemptIso = policy.NextAttempt(task.Attempts + 1, now).ToString("O");
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(task).State = EntityState.Detached;
                continue;
            }

            JsonElement? payload = task.PayloadJson is { } json
                ? JsonSerializer.Deserialize<JsonElement>(json)
                : null;
            var context = IntegrationScheduler.SystemContext(task.TenantId, scope.ServiceProvider);
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runCts.CancelAfter(RunTimeout);
            OutboundResult result;
            try
            {
                result = await OutboundRunner.RunAsync(
                    integration, "retry", context, scope.ServiceProvider, payload, db, runCts.Token);
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                result = OutboundResult.Failure("Timeout");
            }

            task.Attempts++;
            if (result.Ok)
            {
                task.Status = InboxStatus.Processed;
                task.CompletedAtIso = now.ToString("O");
                task.LastError = null;
            }
            else if (policy.IsExhausted(task.Attempts))
            {
                task.Status = InboxStatus.Dead;
                task.LastError = result.Detail;
            }
            else
            {
                task.LastError = result.Detail;   // stays Failed; NextAttemptIso already rolled forward
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
