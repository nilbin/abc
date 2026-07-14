using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The outbox half of docs/09-10: explicit event effects persist in the operation's own
/// transaction (see <see cref="Tam.EntityFrameworkCore.OutboxRecord"/>) and are dispatched
/// asynchronously afterwards — an event exists if and only if its operation committed.
/// The demo transport is the SSE broadcaster; a real bus slots in behind <see cref="IOutboxTransport"/>.
/// </summary>
public interface IOutboxTransport
{
    Task Dispatch(OutboxRecord record, CancellationToken ct);
}

/// <summary>Demo transport: outbox events fan out on the same SSE channel grids listen to.</summary>
public sealed class BroadcasterOutboxTransport(EffectBroadcaster broadcaster) : IOutboxTransport
{
    public Task Dispatch(OutboxRecord record, CancellationToken ct)
    {
        broadcaster.Publish(record.TenantId, record.OperationId,
            [new EventPublished(record.EventType, record.PayloadJson)]);
        return Task.CompletedTask;
    }
}

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    Func<IServiceProvider, DbContext> dbResolver,
    IOutboxTransport transport,
    TamModel? model = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = dbResolver(scope.ServiceProvider);
                var pending = await db.Set<OutboxRecord>()
                    .Where(x => x.DispatchedAtIso == null)
                    .OrderBy(x => x.CreatedAtIso)
                    .Take(50)
                    .ToListAsync(ct);

                foreach (var record in pending)
                {
                    await transport.Dispatch(record, ct);
                    await InvokeSubscribers(record, scope.ServiceProvider, db, ct);
                    record.DispatchedAtIso = DateTimeOffset.UtcNow.ToString("O");
                    // Persist per record: a crash mid-batch must not redeliver what already
                    // went out. Delivery remains at-least-once; subscribers stay idempotent.
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // transient dispatch/db failure: rows stay undispatched and retry next tick
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>
    /// Plugin effect subscribers (docs/22 P2): each runs post-commit in the dispatcher's scope,
    /// only for tenants with the owning plugin active. A failing subscriber never blocks
    /// dispatch or other subscribers — at-most-once, isolated, like any external consumer.
    /// </summary>
    private async Task InvokeSubscribers(
        OutboxRecord record, IServiceProvider services, DbContext db, CancellationToken ct)
    {
        if (model is null || model.Subscribers.Count == 0) return;
        var matching = model.Subscribers.Where(s => s.EventType == record.EventType).ToList();
        if (matching.Count == 0) return;

        var active = await PluginActivations.ActiveAsync(db, record.TenantId, ct);
        var payload = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrEmpty(record.PayloadJson) ? "{}" : record.PayloadJson);
        var effect = new EffectEvent(record.TenantId, record.OperationId, record.EventType, payload.RootElement);

        foreach (var subscriber in matching.Where(s => active.Contains(s.PluginId)))
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
    }
}
