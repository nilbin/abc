using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Approvals;


// Plain rows by design (TAM008 opt-out): a group is a NAMED NODE and a member is a JOIN ROW
// to an external actor — no state machine, no owned children; the one real invariant (the
// nesting/effective-approver policy) is cross-row and lives in ApprovalGroups below.
#pragma warning disable TAM008

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

#pragma warning restore TAM008


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

    public async Task<string?> DescribeAsync(
        Tam.ReachRef reach, Tam.OperationContext context, CancellationToken ct) =>
        Guid.TryParse(reach.Id, out var groupId)
            ? await tam.Db.Set<ApprovalGroup>().Where(g => g.Id == groupId)
                .Select(g => g.Name).SingleOrDefaultAsync(ct)
            : null;
}
