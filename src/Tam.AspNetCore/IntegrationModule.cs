using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Configures the schedule for an outbound integration (docs/25). Only scheduled-trigger
/// integrations of an active plugin can be scheduled; the spec is validated.</summary>
[Operation("integrations.schedule")]
[Authorize("integrations.manage")]
public static class ScheduleIntegration
{
    public sealed record Input(
        [property: LabelKey("labels.integration")] string IntegrationId,
        [property: LabelKey("labels.spec")] string Spec,
        [property: LabelKey("labels.enabled")] bool Enabled = true);

    public sealed record Output(string IntegrationId, string NextRunIso);

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
            x => x.TenantId == context.TenantId.Value && x.IntegrationId == input.IntegrationId, ct);
        if (schedule is null)
        {
            schedule = new IntegrationScheduleEntity
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                IntegrationId = input.IntegrationId,
            };
            tam.Db.Add(schedule);
        }
        schedule.Spec = input.Spec;
        schedule.Enabled = input.Enabled;
        schedule.NextRunIso = next.ToString("O");

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
            .Where(x => x.TenantId == context.TenantId.Value && x.IntegrationId == input.IntegrationId)
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
        [LabelKey("labels.trigger")]
        public string Trigger { get; init; } = "";
        [LabelKey("labels.status")]
        public string Status { get; init; } = "";
        [LabelKey("labels.detail")]
        public string? Detail { get; init; }
        [LabelKey("labels.timestamp")]
        public string RanAt { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var runs = tam.Db.Set<IntegrationRunEntity>()
            .Where(x => x.TenantId == context.TenantId.Value);
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
