using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// Retention janitor (review-round-3): the transient bookkeeping tables — dispatched outbox rows,
/// processed inbox rows and outbound tasks, integration-run history, idempotency records — grow one
/// row per event/call forever and slow the hot loops that scan them. This trims completed rows past
/// a window in a single bulk DELETE each. It deliberately leaves the audit trail and dead-lettered
/// rows untouched: those are records, not scratch space. A real deployment can point this at a SQL
/// job instead; it stays behind the same on/off switch.
/// </summary>
public sealed class RetentionJanitor(
    IServiceScopeFactory scopes,
    Func<IServiceProvider, DbContext> dbResolver,
    TamIntegrationOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.RetentionEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            try { await SweepAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { /* a failed sweep must not kill the loop; next interval retries */ }
            await Task.Delay(options.RetentionInterval, ct);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = dbResolver(scope.ServiceProvider);
        var cutoff = DateTimeOffset.UtcNow - options.RetentionPeriod;
        var cutoffIso = cutoff.ToString("O");

        // A cross-tenant janitor: every sweep opts out of the ambient global filter (there is no
        // request tenant here) and trims across all tenants by timestamp.

        // Delivered events — keep dead-lettered (DeadAtIso) rows for inspection.
        await db.Set<OutboxRecord>().IgnoreQueryFilters()
            .Where(x => x.DispatchedAtIso != null && string.Compare(x.DispatchedAtIso, cutoffIso) < 0)
            .ExecuteDeleteAsync(ct);

        // Successfully processed inbound rows — Dead rows stay for the dead-letter queue.
        await db.Set<InboxRecord>().IgnoreQueryFilters()
            .Where(x => x.Status == InboxStatus.Processed && x.ProcessedAt != null && x.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // Completed outbound retry tasks — Dead tasks stay for the dead-letter queue.
        await db.Set<OutboundTaskEntity>().IgnoreQueryFilters()
            .Where(x => x.Status == InboxStatus.Processed && x.CompletedAtIso != null
                && string.Compare(x.CompletedAtIso, cutoffIso) < 0)
            .ExecuteDeleteAsync(ct);

        // Integration-run audit history (the external-call log, not the domain audit trail).
        await db.Set<IntegrationRunEntity>().IgnoreQueryFilters()
            .Where(x => string.Compare(x.RanAtIso, cutoffIso) < 0)
            .ExecuteDeleteAsync(ct);

        // Idempotency records past the retention window (must exceed the client's retry horizon).
        await db.Set<IdempotencyRecord>().IgnoreQueryFilters()
            .Where(x => x.Timestamp < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
