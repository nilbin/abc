using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static class OutboundFindings
{
    public static readonly FindingFactory Unknown = Finding.Error("integrations.unknown");
    public static readonly FindingFactory NotScheduled = Finding.Error("integrations.not-schedulable");
    public static readonly FindingFactory InvalidSpec = Finding.Error("integrations.invalid-spec");
}

/// <summary>The concrete run context handed to an outbound handler (docs/25).</summary>
internal sealed class IntegrationRunContext(
    OperationContext context, IServiceProvider services, System.Net.Http.HttpClient http,
    SecretVault vault, JsonElement? eventPayload) : IIntegrationRunContext
{
    public OperationContext Context { get; } = context;
    public IServiceProvider Services { get; } = services;
    public System.Net.Http.HttpClient Http { get; } = http;
    public JsonElement? EventPayload { get; } = eventPayload;

    public Task<string?> Setting(string key, CancellationToken ct) =>
        vault.SettingAsync(Context.TenantId.Value, key, ct);

    public Task<string?> Secret(string key, CancellationToken ct) =>
        vault.GetAsync(Context.TenantId.Value, key, ct);
}

/// <summary>
/// Runs one outbound integration and records the run (docs/25). Handler exceptions become a
/// failed run, never an unhandled crash — an external system being down must not take down the
/// pipeline or the scheduler.
/// </summary>
public static class OutboundRunner
{
    public static async Task RunAsync(
        OutboundIntegrationDefinition integration, string trigger,
        OperationContext context, IServiceProvider services, JsonElement? eventPayload,
        DbContext db, CancellationToken ct)
    {
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("tam-integrations");
        var vault = services.GetRequiredService<SecretVault>();
        var run = new IntegrationRunContext(context, services, http, vault, eventPayload);

        OutboundResult result;
        try
        {
            result = await integration.Handler(run, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = OutboundResult.Failure(e.GetType().Name);
        }

        db.Add(new IntegrationRunEntity
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            IntegrationId = integration.Id,
            Trigger = trigger,
            Status = result.Ok ? "ok" : "failed",
            Detail = result.Detail,
            RanAtIso = DateTimeOffset.UtcNow.ToString("O"),
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Cron-lite schedule spec (docs/25): <c>every:15m</c> / <c>every:2h</c> / <c>daily:HH:MM</c> (UTC).</summary>
public static class ScheduleSpec
{
    public static bool IsValid(string spec) => TryNext(spec, DateTimeOffset.UtcNow, out _);

    /// <summary>The next fire strictly after <paramref name="after"/>, or false if the spec is malformed.</summary>
    public static bool TryNext(string spec, DateTimeOffset after, out DateTimeOffset next)
    {
        next = default;
        var parts = spec.Split(':', 2);
        if (parts.Length != 2) return false;

        if (parts[0] == "every")
        {
            var value = parts[1];
            if (value.Length < 2) return false;
            var unit = value[^1];
            if (!int.TryParse(value[..^1], out var n) || n <= 0) return false;
            var interval = unit switch
            {
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                _ => TimeSpan.Zero,
            };
            if (interval == TimeSpan.Zero) return false;
            next = after + interval;
            return true;
        }

        if (parts[0] == "daily")
        {
            if (!TimeOnly.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var time))
                return false;
            var candidate = new DateTimeOffset(after.Date + time.ToTimeSpan(), TimeSpan.Zero);
            if (candidate <= after) candidate = candidate.AddDays(1);
            next = candidate;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Fires scheduled outbound integrations (docs/25). One lightweight loop — no Quartz/Hangfire —
/// ticks each minute, runs every due, enabled schedule whose plugin is active, and rolls the
/// next-run forward. Idempotency and external-side effects are the handler's concern.
/// </summary>
public sealed class IntegrationScheduler(
    IServiceScopeFactory scopes,
    Func<IServiceProvider, DbContext> dbResolver,
    TamModel model) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* a bad tick must not kill the loop */ }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = dbResolver(scope.ServiceProvider);
        var now = DateTimeOffset.UtcNow;

        var due = (await db.Set<IntegrationScheduleEntity>()
                .Where(x => x.Enabled)
                .ToListAsync(ct))
            .Where(x => DateTimeOffset.TryParse(x.NextRunIso, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var n) && n <= now)
            .ToList();

        foreach (var schedule in due)
        {
            if (!model.OutboundIntegrations.TryGetValue(schedule.IntegrationId, out var integration))
                continue;
            var active = await PluginActivations.ActiveAsync(db, schedule.TenantId, ct);
            if (!active.Contains(integration.PluginId)) continue;

            var context = SystemContext(schedule.TenantId, scope.ServiceProvider);
            await OutboundRunner.RunAsync(
                integration, "schedule", context, scope.ServiceProvider, null, db, ct);

            var last = await db.Set<IntegrationRunEntity>()
                .Where(x => x.TenantId == schedule.TenantId && x.IntegrationId == schedule.IntegrationId)
                .OrderByDescending(x => x.RanAtIso).FirstOrDefaultAsync(ct);
            schedule.LastRunIso = now.ToString("O");
            schedule.LastStatus = last?.Status;
            if (ScheduleSpec.TryNext(schedule.Spec, now, out var next))
                schedule.NextRunIso = next.ToString("O");
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>A schedule/event run has no HTTP request; it acts as a system actor with the
    /// permissions the app grants (its own operations still enforce authorization).</summary>
    internal static OperationContext SystemContext(string tenantId, IServiceProvider services) => new()
    {
        Actor = new Actor("system", "System", new HashSet<string> { "*" }),
        TenantId = new TenantId(tenantId),
        Source = InvocationSource.Integration,
        Culture = "en",
        Services = services,
    };
}
