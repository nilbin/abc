using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Approvals;

public static class RequestFindings
{
    /// <summary>The gate's blocking finding — the client renders "submitted for approval".</summary>
    public static readonly FindingFactory Pending = Finding.Error("approvals.pending");
    public static readonly FindingFactory NotPending = Finding.Error("approvals.not-pending");
    public static readonly FindingFactory NotApprover = Finding.Error("approvals.not-approver");
    public static readonly FindingFactory SelfApproval = Finding.Error("approvals.self-approval");
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
            return RequestFindings.NotPending.At(nameof(Input.RequestId));

        // Four-eyes: the initiator never releases their own request, whatever groups they sit in.
        if (request.InitiatorActorId == context.Actor.Id)
            return RequestFindings.SelfApproval.Create();

        var rule = await tam.Db.Set<ApprovalRule>()
            .SingleOrDefaultAsync(r => r.Id == request.RuleId, ct);
        if (rule is null) return PipelineFindings.NotFound.Create();
        var approvers = await ApprovalGroups.EffectiveApproversAsync(tam.Db, rule.GroupId, ct);
        if (!approvers.Contains(context.Actor.Id))
            return RequestFindings.NotApprover.Create();

        request.Approve(context.Actor.Id);

        // The replay itself runs POST-COMMIT via the outbox (docs/09): the approval decision
        // exists if and only if this operation committed, and a redelivered effect is harmless
        // because the replay is idempotent by envelope id (docs/28 seam 3).
        return new Result<Output> { Output = new Output(request.Id, request.Status) }
            .Effect(new EventPublished(new ApprovalApproved(request.Id)));
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
            return RequestFindings.NotPending.At(nameof(Input.RequestId));

        var rule = await tam.Db.Set<ApprovalRule>()
            .SingleOrDefaultAsync(r => r.Id == request.RuleId, ct);
        if (rule is null) return PipelineFindings.NotFound.Create();
        var approvers = await ApprovalGroups.EffectiveApproversAsync(tam.Db, rule.GroupId, ct);
        if (!approvers.Contains(context.Actor.Id))
            return RequestFindings.NotApprover.Create();

        request.Reject(context.Actor.Id, input.Note);
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
