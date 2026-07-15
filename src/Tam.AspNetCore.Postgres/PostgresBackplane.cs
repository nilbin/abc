using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Tam.AspNetCore;

namespace Tam.AspNetCore.Postgres;

/// <summary>
/// Cross-instance SSE backplane over Postgres LISTEN/NOTIFY (docs/12, review-round-3). Publishing an
/// effect NOTIFYs a small envelope on one channel; a background LISTEN loop on EVERY instance
/// receives it and delivers to that instance's subscribers — so a grid open on instance B refreshes
/// from a commit on instance A, with no new infrastructure beyond the database already in use. The
/// event bus (subscribers, outbound integrations) stays the outbox's durable job; this is only the
/// live-refresh edge, so it is deliberately best-effort. Lives in its own package so Npgsql stays out of
/// the framework core — a host adds one reference and one AddTamPostgresBackplane call.
/// </summary>
public sealed class PostgresEffectBackplane(string connectionString, EffectBroadcaster broadcaster)
    : BackgroundService, IEffectBackplane
{
    private const string Channel = "tam_effects";
    private const int MaxPayloadBytes = 7000;   // Postgres NOTIFY payloads cap at 8000 bytes.

    public void Send(string tenantId, string operationId, IReadOnlyList<object> effects)
    {
        if (effects.Count == 0) return;
        var payload = EffectBroadcaster.Payload(tenantId, operationId, effects);
        var envelope = JsonSerializer.Serialize(new { t = tenantId, p = payload });
        if (System.Text.Encoding.UTF8.GetByteCount(envelope) > MaxPayloadBytes)
        {
            // Too large to NOTIFY: refresh at least this node so it isn't lost entirely.
            broadcaster.Deliver(tenantId, payload);
            return;
        }
        _ = NotifyAsync(envelope);   // fire-and-forget: a refresh hint must never fail the operation
    }

    private async Task NotifyAsync(string envelope)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@c, @p)", conn);
            cmd.Parameters.AddWithValue("c", Channel);
            cmd.Parameters.AddWithValue("p", envelope);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort live refresh */ }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                conn.Notification += (_, e) => DeliverEnvelope(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", conn))
                    await listen.ExecuteNonQueryAsync(ct);
                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct);   // parks until a NOTIFY arrives, raising Notification
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(TimeSpan.FromSeconds(2), ct); }   // dropped connection → reconnect
        }
    }

    private void DeliverEnvelope(string envelope)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(envelope);
            broadcaster.Deliver(doc.GetProperty("t").GetString()!, doc.GetProperty("p").GetString()!);
        }
        catch { /* malformed notification — ignore */ }
    }
}

public static class PostgresBackplaneExtensions
{
    /// <summary>Registers the Postgres SSE backplane, overriding the in-process default. Call after
    /// AddTam so this registration wins.</summary>
    public static IServiceCollection AddTamPostgresBackplane(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(sp => new PostgresEffectBackplane(
            connectionString, sp.GetRequiredService<EffectBroadcaster>()));
        services.AddSingleton<IEffectBackplane>(sp => sp.GetRequiredService<PostgresEffectBackplane>());
        services.AddHostedService(sp => sp.GetRequiredService<PostgresEffectBackplane>());
        return services;
    }
}
