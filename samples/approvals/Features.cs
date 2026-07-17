using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Approvals;

public static class ApprovalsFindings
{
    /// <summary>The gate's blocking finding — the client renders "submitted for approval".</summary>
    public static readonly FindingFactory Pending = Finding.Error("approvals.pending");
    public static readonly FindingFactory NotPending = Finding.Error("approvals.not-pending");
    public static readonly FindingFactory NotApprover = Finding.Error("approvals.not-approver");
    public static readonly FindingFactory SelfApproval = Finding.Error("approvals.self-approval");
    public static readonly FindingFactory UnknownGroup = Finding.Error("approvals.unknown-group");
    public static readonly FindingFactory UnknownTarget = Finding.Error("approvals.unknown-target");
    public static readonly FindingFactory NotGateable = Finding.Error("approvals.not-gateable");
    public static readonly FindingFactory UnknownMember = Finding.Error("approvals.unknown-member");
    public static readonly FindingFactory UnknownRule = Finding.Error("approvals.unknown-rule");
}

[Operation("approvals.groups.define")]
[Authorize("approvals.manage")]
public static class DefineGroup
{
    public sealed record Input(
        [property: LabelKey("approvals.labels.name")] string Name,
        [property: LabelKey("approvals.labels.parent-group")] Guid? ParentGroupId = null);

    public sealed record Output(Guid GroupId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        if (input.ParentGroupId is { } parent
            && !await tam.Db.Set<ApprovalGroup>().AnyAsync(g => g.Id == parent, ct))
            return ApprovalsFindings.UnknownGroup.At(nameof(Input.ParentGroupId));

        var group = new ApprovalGroup
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            Name = input.Name,
            ParentGroupId = input.ParentGroupId,
        };
        tam.Db.Add(group);
        return new Output(group.Id);
    }
}

[Operation("approvals.groups.assign")]
[Authorize("approvals.manage")]
public static class AssignMember
{
    public sealed record Input(
        [property: LabelKey("approvals.labels.group")] Guid GroupId,
        [property: LabelKey("approvals.labels.email")] string Email);

    public sealed record Output(Guid GroupId, string ActorId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam,
        Tam.AspNetCore.ITamDirectory directory, CancellationToken ct)
    {
        if (!await tam.Db.Set<ApprovalGroup>().AnyAsync(g => g.Id == input.GroupId, ct))
            return ApprovalsFindings.UnknownGroup.At(nameof(Input.GroupId));

        // The directory seam resolves only accounts that can already ACT in this tenant
        // (docs/26: the tenant boundary is the membership) — the plugin never touches
        // identity tables directly.
        var actorId = await directory.ActorIdByEmailAsync(input.Email, ct);
        if (actorId is null)
            return ApprovalsFindings.UnknownMember.At(nameof(Input.Email));

        if (!await tam.Db.Set<ApprovalGroupMember>()
                .AnyAsync(m => m.GroupId == input.GroupId && m.ActorId == actorId, ct))
            tam.Db.Add(new ApprovalGroupMember
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                GroupId = input.GroupId,
                ActorId = actorId,
            });
        return new Output(input.GroupId, actorId);
    }
}

[Operation("approvals.rules.define")]
[Authorize("approvals.manage")]
public static class DefineRule
{
    public sealed record Input(
        [property: LabelKey("approvals.labels.operation")] string OperationId,
        [property: LabelKey("approvals.labels.group")] Guid GroupId,
        [property: LabelKey("approvals.labels.threshold-field")] string? ThresholdField = null,
        [property: LabelKey("approvals.labels.threshold")] decimal? Threshold = null);

    public sealed record Output(Guid RuleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        // The rule targets a host WIRE id — validated against the compiled model so a typo
        // fails at definition, not silently-never at the gate.
        if (!model.Operations.ContainsKey(input.OperationId))
            return ApprovalsFindings.UnknownTarget.At(nameof(Input.OperationId));
        // Never gate our own surface: an approval rule on approvals.approve would deadlock
        // every release behind a release.
        if (input.OperationId.StartsWith("approvals.", StringComparison.Ordinal))
            return ApprovalsFindings.NotGateable.At(nameof(Input.OperationId));
        if (!await tam.Db.Set<ApprovalGroup>().AnyAsync(g => g.Id == input.GroupId, ct))
            return ApprovalsFindings.UnknownGroup.At(nameof(Input.GroupId));

        var rule = new ApprovalRule
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            OperationId = input.OperationId,
            GroupId = input.GroupId,
            ThresholdField = input.ThresholdField,
            Threshold = input.Threshold,
        };
        tam.Db.Add(rule);
        return new Output(rule.Id);
    }
}

/// <summary>The retire half of the rule registry (docs/34 M5 fix 4): the gate skips retired
/// rules, so retiring UN-GATES the operation — a tenant is never locked into an approval
/// flow it defined. Retire-don't-delete, like every registry artifact.</summary>
[Operation("approvals.rules.retire")]
[Authorize("approvals.manage")]
public static class RetireRule
{
    public sealed record Input([property: LabelKey("approvals.labels.rule")] Guid RuleId);

    public sealed record Output(Guid RuleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var rule = await tam.Db.Set<ApprovalRule>()
            .SingleOrDefaultAsync(r => r.Id == input.RuleId, ct);
        if (rule is null) return ApprovalsFindings.UnknownRule.At(nameof(Input.RuleId));

        rule.Retired = true;
        return new Output(rule.Id);
    }
}

[Operation("approvals.approve")]
[Authorize("approvals.review")]
public static class Approve
{
    public sealed record Input([property: LabelKey("approvals.labels.request")] Guid RequestId);

    public sealed record Output(Guid RequestId, string Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var request = await tam.Db.Set<ApprovalRequest>()
            .SingleOrDefaultAsync(r => r.Id == input.RequestId, ct);
        if (request is null) return PipelineFindings.NotFound.Create();
        if (request.Status != ApprovalRequest.Pending)
            return ApprovalsFindings.NotPending.At(nameof(Input.RequestId));

        // Four-eyes: the initiator never releases their own request, whatever groups they sit in.
        if (request.InitiatorActorId == context.Actor.Id)
            return ApprovalsFindings.SelfApproval.Create();

        var rule = await tam.Db.Set<ApprovalRule>()
            .SingleOrDefaultAsync(r => r.Id == request.RuleId, ct);
        if (rule is null) return PipelineFindings.NotFound.Create();
        var approvers = await ApprovalGroups.EffectiveApproversAsync(tam.Db, rule.GroupId, ct);
        if (!approvers.Contains(context.Actor.Id))
            return ApprovalsFindings.NotApprover.Create();

        request.Status = ApprovalRequest.Approved;
        request.DecidedAtIso = IsoTime.Now();
        request.DecidedByActorId = context.Actor.Id;

        // The replay itself runs POST-COMMIT via the outbox (docs/09): the approval decision
        // exists if and only if this operation committed, and a redelivered effect is harmless
        // because the replay is idempotent by envelope id (docs/28 seam 3).
        return new Result<Output> { Output = new Output(request.Id, request.Status) }
            .Effect(new EventPublished("approvals.approved", new { requestId = request.Id }));
    }
}

[Operation("approvals.reject")]
[Authorize("approvals.review")]
public static class Reject
{
    public sealed record Input(
        [property: LabelKey("approvals.labels.request")] Guid RequestId,
        [property: LabelKey("approvals.labels.note")] string? Note = null);

    public sealed record Output(Guid RequestId, string Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var request = await tam.Db.Set<ApprovalRequest>()
            .SingleOrDefaultAsync(r => r.Id == input.RequestId, ct);
        if (request is null) return PipelineFindings.NotFound.Create();
        if (request.Status != ApprovalRequest.Pending)
            return ApprovalsFindings.NotPending.At(nameof(Input.RequestId));

        var rule = await tam.Db.Set<ApprovalRule>()
            .SingleOrDefaultAsync(r => r.Id == request.RuleId, ct);
        if (rule is null) return PipelineFindings.NotFound.Create();
        var approvers = await ApprovalGroups.EffectiveApproversAsync(tam.Db, rule.GroupId, ct);
        if (!approvers.Contains(context.Actor.Id))
            return ApprovalsFindings.NotApprover.Create();

        request.Status = ApprovalRequest.Rejected;
        request.DecidedAtIso = IsoTime.Now();
        request.DecidedByActorId = context.Actor.Id;
        request.Outcome = input.Note;
        return new Result<Output> { Output = new Output(request.Id, request.Status) };
    }
}

[View("approvals.requests.list")]
[Authorize("approvals.review")]
public static class RequestList
{
    public sealed record Query(string? Status = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("approvals.labels.operation")]
        public string OperationId { get; init; } = "";
        [LabelKey("approvals.labels.initiator")]
        public string Initiator { get; init; } = "";
        [LabelKey("approvals.labels.status")]
        public string Status { get; init; } = "";
        [LabelKey("approvals.labels.created")]
        public string CreatedAtIso { get; init; } = "";
        [LabelKey("approvals.labels.outcome")]
        public string? Outcome { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var requests = tam.Db.Set<ApprovalRequest>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Status))
            requests = requests.Where(r => r.Status == query.Status);
        return requests.Select(r => new Result
        {
            Id = r.Id,
            OperationId = r.OperationId,
            Initiator = r.InitiatorActorId,
            Status = r.Status,
            CreatedAtIso = r.CreatedAtIso,
            Outcome = r.Outcome,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.CreatedAtIso))
        .Filterable(nameof(Result.Status), nameof(Result.OperationId))
        .DefaultSort(nameof(Result.CreatedAtIso), descending: true);
}

[View("approvals.groups.list")]
[Authorize("approvals.manage")]
public static class GroupList
{
    public sealed record Query();

    public sealed record Result
    {
        public Guid Id { get; init; }
        // The assign form's field name, carried deliberately: the grid's RowForm prefills
        // same-named fields, so a row opens the form with its group already bound (docs/32).
        public Guid GroupId { get; init; }
        [LabelKey("approvals.labels.name")]
        public string Name { get; init; } = "";
        [LabelKey("approvals.labels.parent-group")]
        public Guid? ParentGroupId { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam) =>
        tam.Db.Set<ApprovalGroup>().Select(g => new Result
        {
            Id = g.Id, GroupId = g.Id, Name = g.Name, ParentGroupId = g.ParentGroupId,
        });

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Name))
        .DefaultSort(nameof(Result.Name));
}

[View("approvals.rules.list")]
[Authorize("approvals.manage")]
public static class RuleList
{
    public sealed record Query();

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("approvals.labels.operation")]
        public string OperationId { get; init; } = "";
        [LabelKey("approvals.labels.group")]
        public string Group { get; init; } = "";
        [LabelKey("approvals.labels.threshold-field")]
        public string? ThresholdField { get; init; }
        [LabelKey("approvals.labels.threshold")]
        public decimal? Threshold { get; init; }
        public bool Retired { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam) =>
        tam.Db.Set<ApprovalRule>().Select(r => new Result
        {
            Id = r.Id,
            OperationId = r.OperationId,
            Group = tam.Db.Set<ApprovalGroup>()
                .Where(g => g.Id == r.GroupId).Select(g => g.Name).FirstOrDefault() ?? "",
            ThresholdField = r.ThresholdField,
            Threshold = r.Threshold,
            Retired = r.Retired,
        });

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.OperationId))
        .Filterable(nameof(Result.Retired))
        .DefaultSort(nameof(Result.OperationId));
}
