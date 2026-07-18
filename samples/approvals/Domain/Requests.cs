using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Approvals;


/// <summary>
/// The parked envelope (docs/28 approvals seam 2): everything needed to replay the blocked
/// attempt later — operation id, raw wire body, the pipeline's payload hash (also the dedupe
/// key: a retried submit re-blocks but never double-parks), initiator, culture. Status walks
/// pending → approved → executed/failed, or pending → rejected; the decided-by column plus the
/// replay's audit correlation gives dual attribution.
/// </summary>
public sealed class ApprovalRequest : ITenantScoped
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Executed = "executed";
    public const string Failed = "failed";

    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Guid RuleId { get; private set; }
    public string OperationId { get; private set; } = "";
    public string BodyJson { get; private set; } = "";
    public string PayloadHash { get; private set; } = "";
    public string InitiatorActorId { get; private set; } = "";
    public string Culture { get; private set; } = "";
    public string Status { get; private set; } = Pending;
    public string CreatedAtIso { get; private set; } = "";
    public string? DecidedAtIso { get; private set; }
    public string? DecidedByActorId { get; private set; }
    /// <summary>Replay outcome: the audit reference on success, the first error code on failure,
    /// or the reviewer's note on rejection.</summary>
    public string? Outcome { get; private set; }

    public static ApprovalRequest Park(
        string tenantId, Guid ruleId, string operationId, string bodyJson,
        string payloadHash, string initiatorActorId, string culture) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        RuleId = ruleId,
        OperationId = operationId,
        BodyJson = bodyJson,
        PayloadHash = payloadHash,
        InitiatorActorId = initiatorActorId,
        Culture = culture,
        CreatedAtIso = IsoTime.Now(),
    };

    // The DECISION fields move atomically — no half-decided request is representable (the
    // invariant this entity owns). Whether the decider MAY decide (approver set, self-
    // approval) needs the database and stays in the operations — the ERP idiom.
    public void Approve(string decidedByActorId) => Decide(Approved, decidedByActorId, null);

    public void Reject(string decidedByActorId, string? note) =>
        Decide(Rejected, decidedByActorId, note);

    /// <summary>Replay settlement: executed with the audit reference, or failed with the
    /// first error code.</summary>
    public void CloseOut(bool failed, string? outcome)
    {
        Status = failed ? Failed : Executed;
        Outcome = outcome;
    }

    private void Decide(string status, string decidedByActorId, string? outcome)
    {
        Status = status;
        DecidedAtIso = IsoTime.Now();
        DecidedByActorId = decidedByActorId;
        Outcome = outcome;
    }
}


// The aggregate's published language (docs/31 "events are records").

[DomainEvent("approvals.requested")]
public sealed record ApprovalRequested(Guid RequestId);

[DomainEvent("approvals.approved")]
public sealed record ApprovalApproved(Guid RequestId);
