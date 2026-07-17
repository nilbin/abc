using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Integration operations surface (docs/25): schedules, manual runs, run history,
/// dead-letter + requeue. The drivers (inbox, retry queue, scheduler, outbox) are core loops.</summary>
[TamPackage("tam.integrations", "integrations", "web.integrations")]
public sealed class TamIntegrationsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin
            .AddOperationType(typeof(ScheduleIntegration))
            .AddOperationType(typeof(RunIntegration))
            .AddOperationType(typeof(RequeueDeadLetter))
            .AddViewType(typeof(IntegrationRunList))
            .AddViewType(typeof(DeadLetterList));
    }
}

/// <summary>Configures the schedule for an outbound integration (docs/25). Only scheduled-trigger
/// integrations of an active plugin can be scheduled; the spec is validated.</summary>
[Operation("integrations.schedule")]
[Authorize("integrations.manage")]
public static class ScheduleIntegration
{
    public sealed record Input(
        [property: LabelKey("labels.integration")] string IntegrationId,
        string Spec,
        bool Enabled = true);

    public sealed record Output(
        string IntegrationId,
        // The Iso suffix is storage encoding, not product vocabulary — relabel, keep the wire name.
        [property: LabelKey("labels.next-run")] string NextRunIso);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        if (!model.OutboundIntegrations.TryGetValue(input.IntegrationId, out var integration)
            || integration.Trigger is not ScheduleTrigger)
            return OutboundFindings.NotScheduled.With(("integration", input.IntegrationId))
                .At(nameof(Input.IntegrationId));

        var active = await PluginActivations.ActiveAsync(tam.Db, context.TenantId.Value, ct);
        if (!active.Contains(integration.PluginId))
            return OutboundFindings.Unknown.With(("integration", input.IntegrationId))
                .At(nameof(Input.IntegrationId));

        if (!ScheduleSpec.TryNext(input.Spec, DateTimeOffset.UtcNow, out var next))
            return OutboundFindings.InvalidSpec.At(nameof(Input.Spec));

        var schedule = await tam.Db.Set<IntegrationScheduleEntity>().SingleOrDefaultAsync(
            x => x.IntegrationId == input.IntegrationId, ct);
        if (schedule is null)
        {
            schedule = new IntegrationScheduleEntity
            {
                Id = Guid.NewGuid(),
                IntegrationId = input.IntegrationId,
            };
            tam.Db.Add(schedule);
        }
        schedule.Spec = input.Spec;
        schedule.Enabled = input.Enabled;
        schedule.NextRunIso = IsoTime.From(next);

        return new Output(input.IntegrationId, schedule.NextRunIso);
    }
}

/// <summary>Fires an outbound integration on demand (docs/25) — the manual trigger, and a way to
/// test a scheduled/event one. Runs synchronously and records the run.</summary>
[Operation("integrations.run")]
[Authorize("integrations.manage")]
public static class RunIntegration
{
    public sealed record Input([property: LabelKey("labels.integration")] string IntegrationId);

    public sealed record Output(string IntegrationId, string Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        if (!model.OutboundIntegrations.TryGetValue(input.IntegrationId, out var integration))
            return OutboundFindings.Unknown.With(("integration", input.IntegrationId))
                .At(nameof(Input.IntegrationId));

        var active = await PluginActivations.ActiveAsync(tam.Db, context.TenantId.Value, ct);
        if (!active.Contains(integration.PluginId))
            return OutboundFindings.Unknown.With(("integration", input.IntegrationId))
                .At(nameof(Input.IntegrationId));

        await OutboundRunner.RunAsync(
            integration, "manual", context, context.Services!, null, tam.Db, ct);

        var last = await tam.Db.Set<IntegrationRunEntity>()
            .Where(x => x.IntegrationId == input.IntegrationId)
            .OrderByDescending(x => x.RanAtIso).FirstOrDefaultAsync(ct);
        return new Output(input.IntegrationId, last?.Status ?? "ok");
    }
}

/// <summary>The tenant's outbound-integration run history (docs/25) — every external call, audited.</summary>
[View("integrations.runs")]
[Authorize("integrations.manage")]
public static class IntegrationRunList
{
    public sealed record Query(string? IntegrationId = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.integration")]
        public string IntegrationId { get; init; } = "";
        public string Trigger { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Detail { get; init; }
        [LabelKey("labels.timestamp")]
        public string RanAt { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var runs = tam.Db.Set<IntegrationRunEntity>().AsQueryable();
        if (query.IntegrationId is { Length: > 0 } id)
            runs = runs.Where(x => x.IntegrationId == id);
        return runs.Select(x => new Result
        {
            Id = x.Id, IntegrationId = x.IntegrationId, Trigger = x.Trigger,
            Status = x.Status, Detail = x.Detail, RanAt = x.RanAtIso,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.RanAt), nameof(Result.IntegrationId))
        .Filterable(nameof(Result.IntegrationId), nameof(Result.Status))
        .DefaultSort(nameof(Result.RanAt), descending: true);
}

/// <summary>
/// The unified dead-letter queue (docs/25): rows the retry machinery gave up on — inbound rows
/// that failed their operation past the cap, and outbound pushes that failed past the cap — so an
/// operator can see what needs a fix and requeue it once the root cause is resolved.
/// </summary>
[View("integrations.dead-letter")]
[Authorize("integrations.manage")]
public static class DeadLetterList
{
    public sealed record Query(string? Kind = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.direction")]
        public string Kind { get; init; } = "";        // inbound | outbound
        [LabelKey("labels.integration")]
        public string IntegrationId { get; init; } = "";
        public string Reference { get; init; } = "";    // inbound: idempotency key; outbound: trigger
        public int Attempts { get; init; }
        [LabelKey("labels.detail")]
        public string? LastError { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var tenant = context.TenantId.Value;
        var inbound = query.Kind == "outbound" ? [] : tam.Db.Set<InboxRecord>()
            .Where(x => x.Status == InboxStatus.Dead)
            .Select(x => new Result
            {
                Id = x.Id, Kind = "inbound", IntegrationId = x.IntegrationId,
                Reference = x.Key, Attempts = x.Attempts, LastError = x.LastError,
            }).ToList();
        var outbound = query.Kind == "inbound" ? [] : tam.Db.Set<OutboundTaskEntity>()
            .Where(x => x.Status == InboxStatus.Dead)
            .Select(x => new Result
            {
                Id = x.Id, Kind = "outbound", IntegrationId = x.IntegrationId,
                Reference = x.Trigger, Attempts = x.Attempts, LastError = x.LastError,
            }).ToList();
        return inbound.Concat(outbound).AsQueryable();
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.IntegrationId), nameof(Result.Attempts))
        .Filterable(nameof(Result.Kind), nameof(Result.IntegrationId))
        .DefaultSort(nameof(Result.IntegrationId));
}

/// <summary>
/// Requeues a dead-lettered item (docs/25) once its root cause is fixed: attempts reset to zero and
/// it becomes retryable. An outbound task is picked up by the retry driver within its interval; an
/// inbound row is re-driven on the next inbound call for that integration (the inbox drains under
/// the caller's actor, so it has no background driver of its own).
/// </summary>
[Operation("integrations.requeue")]
[Authorize("integrations.manage")]
public static class RequeueDeadLetter
{
    public sealed record Input(Guid Id);

    public sealed record Output(Guid Id, string Kind);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var tenant = context.TenantId.Value;

        var inbox = await tam.Db.Set<InboxRecord>().SingleOrDefaultAsync(
            x => x.Id == input.Id, ct);
        if (inbox is not null)
        {
            inbox.Status = InboxStatus.Pending;
            inbox.Attempts = 0;
            inbox.NextAttemptIso = null;
            inbox.LastError = null;
            return new Output(input.Id, "inbound");
        }

        var task = await tam.Db.Set<OutboundTaskEntity>().SingleOrDefaultAsync(
            x => x.Id == input.Id, ct);
        if (task is not null)
        {
            task.Status = InboxStatus.Failed;
            task.Attempts = 0;
            task.NextAttemptIso = IsoTime.Now();   // due immediately
            task.LastError = null;
            return new Output(input.Id, "outbound");
        }

        return OutboundFindings.NotFound.At(nameof(Input.Id));
    }
}
