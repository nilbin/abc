using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace Tam.AspNetCore;

/// <summary>
/// Decision D5's cheap 90%: committed operation effects fan out over one SSE channel so open
/// grids and forms can refresh instead of polling. No presence, no co-editing — the three-way
/// merge already makes concurrent edits safe.
/// </summary>
public sealed class EffectBroadcaster
{
    private readonly ConcurrentDictionary<Guid, (string TenantId, Channel<string> Channel)> subscribers = new();

    /// <summary>The SSE wire payload for a set of effects (also what a backplane transports).</summary>
    public static string Payload(string tenantId, string operationId, IReadOnlyList<object> effects) =>
        JsonSerializer.Serialize(new { tenantId, operationId, effects }, TamJson.Options);

    /// <summary>Fans a pre-built payload out to THIS instance's tenant-matched subscribers. The
    /// backplane calls this both for locally-published effects and for effects that arrived from
    /// another instance (Postgres NOTIFY), so cross-instance delivery is just "Deliver on receipt".</summary>
    public void Deliver(string tenantId, string payload)
    {
        if (subscribers.IsEmpty) return;
        foreach (var (subscriberTenant, channel) in subscribers.Values)
            if (subscriberTenant == tenantId)
                channel.Writer.TryWrite(payload);
    }

    public async Task Stream(HttpContext http, CancellationToken ct)
    {
        // The pinned ambient tenant (incl. an act-as rebind) — the channel this client listens on.
        var tenant = TamTenant.Resolve(http).Value;
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        subscribers[id] = (tenant, channel);

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        try
        {
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            await foreach (var payload in channel.Reader.ReadAllAsync(ct))
            {
                await http.Response.WriteAsync($"data: {payload}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // client went away — normal
        }
        finally
        {
            subscribers.TryRemove(id, out _);
        }
    }
}
