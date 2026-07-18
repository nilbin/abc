using Microsoft.EntityFrameworkCore;
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

    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid RuleId { get; set; }
    public string OperationId { get; set; } = "";
    public string BodyJson { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string InitiatorActorId { get; set; } = "";
    public string Culture { get; set; } = "";
    public string Status { get; set; } = Pending;
    public string CreatedAtIso { get; set; } = "";
    public string? DecidedAtIso { get; set; }
    public string? DecidedByActorId { get; set; }
    /// <summary>Replay outcome: the audit reference on success, the first error code on failure,
    /// or the reviewer's note on rejection.</summary>
    public string? Outcome { get; set; }
}
