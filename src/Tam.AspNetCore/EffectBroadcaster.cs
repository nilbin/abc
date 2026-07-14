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
    private readonly ConcurrentDictionary<Guid, Channel<string>> subscribers = new();

    public void Publish(string tenantId, string operationId, IReadOnlyList<object> effects)
    {
        if (subscribers.IsEmpty || effects.Count == 0) return;
        var payload = JsonSerializer.Serialize(
            new { tenantId, operationId, effects }, TamJson.Options);
        foreach (var channel in subscribers.Values)
            channel.Writer.TryWrite(payload);
    }

    public async Task Stream(HttpContext http, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        subscribers[id] = channel;

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
