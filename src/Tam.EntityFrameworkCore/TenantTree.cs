using Microsoft.EntityFrameworkCore;

namespace Tam.EntityFrameworkCore;

/// <summary>A node the account may act in, with its human label ("Demo AB ▸ Norrservice Nord AB").</summary>
public sealed record StandableNode(string Id, string Display, string Path);

/// <summary>Hierarchy membership questions shared by the auth server, the act-as middleware and the
/// standable-nodes endpoint.</summary>
public static class TenantTree
{
    /// <summary>
    /// Every node the account may STAND at, path-ordered and labeled by the display-name chain:
    /// membership nodes plus all descendants of memberships carrying a cascading role (docs/26
    /// D-H3/D-H5). Backs the login tenant picker and the SPA's create-target/company pickers.
    /// (The tenants table is small; a very large tree would page/search instead of listing.)
    /// </summary>
    public static IReadOnlyList<StandableNode> StandableNodes(DbContext db, Guid accountId)
    {
        var memberships = db.Set<TenantMembershipEntity>().AcrossTenants()
            .Where(m => m.AccountId == accountId && m.Active)
            .ToList();
        if (memberships.Count == 0) return [];

        var memberNodeIds = memberships.Select(m => m.TenantId).ToHashSet();
        var allTenants = db.Set<TenantEntity>().ToList();
        var byId = allTenants.ToDictionary(t => t.Id);
        var cascadingRootPaths = memberships
            .Where(m => m.Roles().Any(a => a.Cascade))
            .Select(m => byId.TryGetValue(m.TenantId, out var root) ? root.Path : null)
            .OfType<string>()
            .ToList();

        string Label(TenantEntity node) => string.Join(" ▸ ", node.AncestorIds()
            .Select(id => byId.TryGetValue(id, out var t) ? t.DisplayName : id));

        var nodes = allTenants
            .Where(t => memberNodeIds.Contains(t.Id)
                || cascadingRootPaths.Any(p => TenantEntity.IsSelfOrDescendant(p, t.Path)))
            .OrderBy(t => t.Path, StringComparer.Ordinal)
            .Select(t => new StandableNode(t.Id, Label(t), t.Path))
            .ToList();
        // A membership node with no TenantEntity row (pre-hierarchy data) is still standable.
        nodes.AddRange(memberships
            .Where(m => !byId.ContainsKey(m.TenantId))
            .Select(m => new StandableNode(m.TenantId, m.TenantId, m.TenantId)));
        return nodes;
    }
    /// <summary>
    /// May this account STAND at (act in) <paramref name="tenantId"/>? True for a node the account
    /// has an active membership in, and for any descendant of a membership carrying at least one
    /// CASCADING role (docs/26 D-H3/D-H5) — the region admin acts in a sub-company with no
    /// membership row there. Grants never flow up, so an ancestor of a membership is NOT standable.
    /// </summary>
    public static bool IsStandable(DbContext db, Guid accountId, string tenantId) =>
        StandableNodes(db, accountId).Any(n => n.Id == tenantId);
}
