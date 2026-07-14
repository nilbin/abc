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

    public void Publish(string tenantId, string operationId, IReadOnlyList<object> effects)
    {
        if (subscribers.IsEmpty || effects.Count == 0) return;
        var payload = JsonSerializer.Serialize(
            new { tenantId, operationId, effects }, TamJson.Options);
        // Tenant-scoped delivery: a listener only ever receives its own tenant's effects.
        foreach (var (subscriberTenant, channel) in subscribers.Values)
            if (subscriberTenant == tenantId)
                channel.Writer.TryWrite(payload);
    }

    public async Task Stream(HttpContext http, CancellationToken ct)
    {
        var tenant = http.RequestServices
            .GetService(typeof(ITenantProvider)) is ITenantProvider tenants
            ? tenants.GetTenant(http).Value
            : "";
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
