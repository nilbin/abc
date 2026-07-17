using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Approvals;

/// <summary>
/// An approver group — NESTED if the tenant wants (docs/28 D-AG3/D-AG4: nesting semantics are
/// the PLUGIN's problem; the framework never learns about groups). A member of a subgroup can
/// approve anything its ancestors can — see <see cref="ApprovalGroups.EffectiveApproversAsync"/>.
/// </summary>
public sealed class ApprovalGroup : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? ParentGroupId { get; set; }
}

/// <summary>Membership by actor id — the one subject-side fact the framework provides (docs/28).</summary>
public sealed class ApprovalGroupMember : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid GroupId { get; set; }
    public string ActorId { get; set; } = "";
}

/// <summary>
/// Which HOST operations need sign-off — tenant data over host WIRE ids, exactly the config the
/// wildcard gate reads. An optional numeric threshold restricts the rule to inputs whose named
/// wire field reaches the limit ("orders.create above 100 000 kr").
/// </summary>
public sealed class ApprovalRule : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public Guid GroupId { get; set; }
    public string? ThresholdField { get; set; }     // wire name, e.g. "estimatedTotal"
    public decimal? Threshold { get; set; }
    public bool Retired { get; set; }
}

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

public static class ApprovalGroups
{
    /// <summary>
    /// The effective approver set of a group: its direct members plus the members of every
    /// DESCENDANT subgroup — a north-region lead sits in "leads.north" and thereby approves
    /// what "leads" approves. One tenant-wide load (the ambient tenant filter scopes both
    /// queries) and an in-memory walk: approval groups are tens of rows, not thousands
    /// (the docs/24 seat profile).
    /// </summary>
    public static async Task<IReadOnlySet<string>> EffectiveApproversAsync(
        DbContext db, Guid groupId, CancellationToken ct)
    {
        var groups = await db.Set<ApprovalGroup>().ToListAsync(ct);
        var members = await db.Set<ApprovalGroupMember>().ToListAsync(ct);

        var byParent = groups.Where(g => g.ParentGroupId is not null)
            .ToLookup(g => g.ParentGroupId!.Value);
        var reachable = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(groupId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reachable.Add(id)) continue;   // cycle-safe: a repeated node is skipped
            foreach (var child in byParent[id]) stack.Push(child.Id);
        }

        return members.Where(m => reachable.Contains(m.GroupId))
            .Select(m => m.ActorId).ToHashSet();
    }
}

/// <summary>
/// The plugin's reach provider (docs/35): `approvals.group:{id}` — containment is the group's
/// EFFECTIVE approver set (direct members plus every descendant subgroup's, the same
/// resolution the gate uses), so host domains referencing a group inherit the plugin's
/// nesting semantics without learning them.
/// </summary>
public sealed class GroupReach(Tam.EntityFrameworkCore.ITamDb tam) : Tam.IReachProvider
{
    public async Task<bool> ContainsAsync(
        Tam.ReachRef reach, Tam.OperationContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(reach.Id, out var groupId)) return false;
        var approvers = await ApprovalGroups.EffectiveApproversAsync(tam.Db, groupId, ct);
        return approvers.Contains(context.Actor.Id);
    }

    public async Task<IReadOnlyList<Tam.ReachOption>> SearchAsync(
        string? search, Tam.OperationContext context, CancellationToken ct)
    {
        var groups = tam.Db.Set<ApprovalGroup>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            groups = groups.Where(g => g.Name.Contains(search!));
        var rows = await groups.OrderBy(g => g.Name).Take(50).ToListAsync(ct);
        return rows.Select(g => new Tam.ReachOption(
            new Tam.ReachRef("approvals.group", g.Id.ToString()), g.Name)).ToList();
    }
}
