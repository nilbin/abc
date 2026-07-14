using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The outbox half of docs/09-10: explicit event effects persist in the operation's own
/// transaction (see <see cref="Tam.EntityFrameworkCore.OutboxRecord"/>) and are dispatched
/// asynchronously afterwards — an event exists if and only if its operation committed. Its job is
/// the durable event consumers (plugin subscribers and outbound integrations); live SSE refresh is
/// the inline publish through <see cref="IEffectBackplane"/>, so the outbox no longer re-broadcasts
/// (that was a duplicate send — every event went out twice).
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    Func<IServiceProvider, DbContext> dbResolver,
    TamModel? model = null) : BackgroundService
{
    /// <summary>How long a claimed row is reserved to this instance before the lease lapses and
    /// another instance may re-deliver (bounds redelivery latency after a crash mid-dispatch).</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    /// <summary>Poison cap: a row that keeps throwing is dead-lettered instead of blocking the stream.</summary>
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { /* transient dispatch/db failure: rows stay undispatched and retry next tick */ }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = dbResolver(scope.ServiceProvider);
        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");

        // Cross-tenant background scan: no ambient tenant, so opt out of the global filter and
        // dispatch each row under its own TenantId (read from the row).
        var pending = await db.Set<OutboxRecord>()
            .IgnoreQueryFilters()
            .Where(x => x.DispatchedAtIso == null && x.DeadAtIso == null
                && (x.ClaimedUntilIso == null || string.Compare(x.ClaimedUntilIso, nowIso) < 0))
            .OrderBy(x => x.CreatedAtIso)
            .Take(50)
            .ToListAsync(ct);

        // One activation lookup per tenant per tick, shared across records and subscribers.
        var activations = new Dictionary<string, IReadOnlySet<string>>();

        foreach (var record in pending)
        {
            // Claim under the concurrency token before dispatching: only one instance delivers a
            // given row. A racing instance collides on ClaimedUntilIso and skips.
            record.ClaimedUntilIso = now.Add(Lease).ToString("O");
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(record).State = EntityState.Detached;
                continue;
            }

            try
            {
                await InvokeSubscribers(record, scope.ServiceProvider, db, activations, ct);
                record.DispatchedAtIso = DateTimeOffset.UtcNow.ToString("O");
                record.ClaimedUntilIso = null;
                // Persist per record: a crash mid-batch must not redeliver what already went out.
                // Delivery remains at-least-once; subscribers stay idempotent.
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // Poison isolation: this row failed, so bump its attempts and release the lease so
                // it does not block newer rows. Dead-letter after the cap. Newer rows in this same
                // batch still get their own claim/try — one bad payload no longer wedges the stream.
                record.Attempts++;
                record.LastError = e.GetType().Name;
                record.ClaimedUntilIso = null;
                if (record.Attempts >= MaxAttempts)
                    record.DeadAtIso = DateTimeOffset.UtcNow.ToString("O");
                try { await db.SaveChangesAsync(ct); }
                catch { db.Entry(record).State = EntityState.Detached; }
            }
        }
    }

    /// <summary>
    /// Plugin effect subscribers (docs/22 P2): each runs post-commit in the dispatcher's scope,
    /// only for tenants with the owning plugin active. A failing subscriber never blocks
    /// dispatch or other subscribers — at-most-once, isolated, like any external consumer.
    /// </summary>
    private async Task InvokeSubscribers(
        OutboxRecord record, IServiceProvider services, DbContext db,
        Dictionary<string, IReadOnlySet<string>> activations, CancellationToken ct)
    {
        if (model is null || model.Subscribers.Count == 0 && model.OutboundIntegrations.Count == 0) return;

        if (!activations.TryGetValue(record.TenantId, out var active))
            activations[record.TenantId] = active = await PluginActivations.ActiveAsync(db, record.TenantId, ct);
        var payload = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrEmpty(record.PayloadJson) ? "{}" : record.PayloadJson);
        var effect = new EffectEvent(record.TenantId, record.OperationId, record.EventType, payload.RootElement);

        foreach (var subscriber in model.Subscribers
            .Where(s => s.EventType == record.EventType && active.Contains(s.PluginId)))
        {
            try
            {
                await subscriber.Handler(effect, services, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // an unhealthy plugin must not take down dispatch — the row still completes
            }
        }

        // Event-triggered outbound integrations (docs/25): push to external systems on commit.
        // Each runs under its own deadline so a slow endpoint can't stall the dispatch loop, and a
        // failure enqueues an outbound task so the retry driver re-drives it with backoff.
        var outbound = model.OutboundIntegrations.Values
            .Where(i => i.Trigger is EventTrigger e && e.EventType == record.EventType
                && active.Contains(i.PluginId))
            .ToList();
        if (outbound.Count == 0) return;

        var policy = RetryPolicy.Resolve(services);
        var context = IntegrationScheduler.SystemContext(record.TenantId, services);
        foreach (var integration in outbound)
        {
            OutboundResult result;
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runCts.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                result = await OutboundRunner.RunAsync(
                    integration, "event", context, services, effect.Payload, db, runCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { result = OutboundResult.Failure("Timeout"); }
            catch (Exception e) { result = OutboundResult.Failure(e.GetType().Name); }

            if (!result.Ok)
                await OutboundRetryQueue.EnqueueFailureAsync(
                    db, policy, record.TenantId, integration.Id, "event",
                    effect.Payload.GetRawText(), result.Detail, ct);
        }
    }
}
