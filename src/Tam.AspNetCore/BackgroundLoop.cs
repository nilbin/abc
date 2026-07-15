using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Tam.AspNetCore;

/// <summary>
/// The ONE resilient background-loop shape every Tam driver shares (outbox, retry queue,
/// scheduler, janitors): tick, swallow transient failures — a bad tick must never kill the loop —
/// wait the interval, repeat. Cancellation ends the loop; everything else retries next tick.
/// </summary>
public abstract class TamBackgroundLoop(TimeSpan interval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Transient failure: pending work stays pending and the next tick retries.
            }
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    protected abstract Task TickAsync(CancellationToken ct);
}

/// <summary>
/// The claim-lease commit shared by every competing consumer (outbox rows, retry tasks, schedule
/// ticks): the caller rolls the row's lease field forward, then commits here under the row's
/// concurrency token. False means another instance won the race — the row is DETACHED (so the
/// stale copy can never be written later in the batch) and the caller skips it.
/// </summary>
public static class ClaimLease
{
    public static async Task<bool> TryCommitAsync(DbContext db, object entity, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }
}
