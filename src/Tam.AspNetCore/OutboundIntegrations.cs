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
    public static readonly FindingFactory NotFound = Finding.Error("integrations.dead-letter-not-found");
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
    public static async Task<OutboundResult> RunAsync(
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            // A handler that runs long or hangs is cancelled by a per-run deadline (the scheduler
            // passes a timeout token); that surfaces here as a failed run, not a stuck loop.
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
        return result;
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
        if (string.IsNullOrEmpty(spec)) return false;
        var parts = spec.Split(':', 2);
        if (parts.Length != 2) return false;

        if (parts[0] == "every")
        {
            var value = parts[1];
            if (value.Length < 2) return false;
            var unit = value[^1];
            if (!int.TryParse(value[..^1], out var n) || n <= 0) return false;
            // A very large interval (every:2000000000d) overflows TimeSpan/DateTimeOffset; a malformed
            // spec must fail closed, not throw into the scheduler tick. Both the TimeSpan.From* build
            // and the addition can throw, so the whole computation is guarded.
            try
            {
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
            catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException)
            {
                return false;
            }
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

    /// <summary>A single scheduled handler gets this long before its per-run token trips — a hung
    /// external call fails that run, it does not wedge the once-a-minute tick behind it.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = dbResolver(scope.ServiceProvider);
        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");

        // Push the ordering into the index; parse-and-filter only the small due set. NextRunIso is
        // ISO-8601 UTC ("O"), so string ordering is chronological ordering.
        var due = (await db.Set<IntegrationScheduleEntity>()
                .Where(x => x.Enabled && string.Compare(x.NextRunIso, nowIso) <= 0)
                .OrderBy(x => x.NextRunIso)
                .ToListAsync(ct))
            .Where(x => DateTimeOffset.TryParse(x.NextRunIso, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var n) && n <= now)
            .ToList();

        // One activation lookup per tenant per tick, not per due schedule (review N+1).
        var activations = new Dictionary<string, IReadOnlySet<string>>();

        foreach (var schedule in due)
        {
            if (!model.OutboundIntegrations.TryGetValue(schedule.IntegrationId, out var integration))
                continue;
            if (!activations.TryGetValue(schedule.TenantId, out var active))
                activations[schedule.TenantId] = active = await PluginActivations.ActiveAsync(db, schedule.TenantId, ct);
            if (!active.Contains(integration.PluginId)) continue;

            // Claim first: roll NextRunIso forward and commit under the concurrency token BEFORE
            // running. If another instance already claimed this tick the token has changed and the
            // save throws — we skip, so the handler fires at most once across the fleet.
            schedule.NextRunIso = ScheduleSpec.TryNext(schedule.Spec, now, out var next)
                ? next.ToString("O")
                : now.AddYears(100).ToString("O");   // unparseable spec: park it, don't hot-loop
            schedule.LastRunIso = nowIso;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(schedule).State = EntityState.Detached;
                continue;
            }

            var context = SystemContext(schedule.TenantId, scope.ServiceProvider);
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runCts.CancelAfter(RunTimeout);
            OutboundResult result;
            try
            {
                result = await OutboundRunner.RunAsync(
                    integration, "schedule", context, scope.ServiceProvider, null, db, runCts.Token);
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                result = OutboundResult.Failure("Timeout");
            }

            schedule.LastStatus = result.Ok ? "ok" : "failed";
            if (!result.Ok)
                await OutboundRetryQueue.EnqueueFailureAsync(
                    db, RetryPolicy.Resolve(scope.ServiceProvider),
                    schedule.TenantId, schedule.IntegrationId, "schedule", null, result.Detail, ct);
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
