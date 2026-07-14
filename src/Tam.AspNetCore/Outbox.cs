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
    IOutboxTransport transport) : BackgroundService
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
                    record.DispatchedAtIso = DateTimeOffset.UtcNow.ToString("O");
                }
                if (pending.Count > 0) await db.SaveChangesAsync(ct);
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
}
