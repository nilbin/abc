using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static class SubscriptionFindings
{
    public static readonly FindingFactory NotEntitled = Finding.Error("subscriptions.not-entitled");
    public static readonly FindingFactory SeatLimit = Finding.Error("subscriptions.seat-limit");
    /// <summary>tenants.move warning: an active plugin lost its entitlement under the new anchor.</summary>
    public static readonly FindingFactory EntitlementLost = Finding.Warning("subscriptions.entitlement-lost");
    /// <summary>tenants.move warning: the target anchor's seat pool is over its ceiling.</summary>
    public static readonly FindingFactory SeatOverflow = Finding.Warning("subscriptions.seat-overflow");
}

/// <summary>The subscription governing a node, and WHICH node anchors it (docs/24 hierarchy).</summary>
public sealed record CoveringSubscription(SubscriptionEntity Subscription, string AnchorTenantId);

/// <summary>
/// Subscription resolution in a tenant tree (docs/24, the ANCHOR model): a subscription row IS an
/// anchor covering its own node and every descendant, until a NEARER anchor shadows it — the money
/// cascades, exactly as capability does (docs/26), while plugin activation stays a per-node choice.
/// Resolution is the nearest ancestor-or-self anchor on the materialized path — the same chain walk
/// actor grants use. Nearest wins, full stop: no entitlement unions, no seat borrowing across
/// anchors. A chain with NO anchor gets ONE free default for the whole tree, anchored at the ROOT —
/// the framework works fully without billing, but a tree is one commercial standing (creating child
/// nodes never mints fresh free seats).
/// </summary>
/// <summary>
/// The enforcement baseline when a tree has NO subscription anywhere on its chain — not a
/// product tier, but the answer to "what does the gate do when billing is silent". The shipped
/// default is conservative (bootstrap-sized: enough to create an admin and evaluate, capped so
/// ignoring billing is never a bypass). A deployment that simply doesn't do billing sets
/// <see cref="Unlimited"/> at startup: it has no vendor to bypass.
/// </summary>
public sealed record SubscriptionDefaults(string Plan, int Seats, IReadOnlyList<string> Entitlements)
{
    /// <summary>No caps, all entitlements — for self-hosted deployments without billing.</summary>
    public static SubscriptionDefaults Unlimited { get; } = new("self-hosted", int.MaxValue, ["*"]);
}

public static class Subscriptions
{
    /// <summary>Host-configurable no-billing baseline; set once at startup, before traffic.</summary>
    public static SubscriptionDefaults Defaults { get; set; } = new("free", 2, []);

    public static async Task<CoveringSubscription> CoveringAsync(
        DbContext db, string tenantId, CancellationToken ct)
    {
        var chain = Chain(await db.Set<TenantEntity>().FindAsync([tenantId], ct), tenantId);
        // Subscription rows along the chain are cross-tenant by nature — deliberate filter opt-out.
        var rows = await db.Set<SubscriptionEntity>().IgnoreQueryFilters()
            .Where(s => chain.Contains(s.TenantId)).ToListAsync(ct);
        return Pick(chain, rows);
    }

    /// <summary>Sync twin for views (IQueryable pipelines are synchronous).</summary>
    public static CoveringSubscription Covering(DbContext db, string tenantId)
    {
        var chain = Chain(db.Set<TenantEntity>().Find(tenantId), tenantId);
        var rows = db.Set<SubscriptionEntity>().IgnoreQueryFilters()
            .Where(s => chain.Contains(s.TenantId)).ToList();
        return Pick(chain, rows);
    }

    /// <summary>
    /// The tenant ids an anchor covers: its subtree MINUS any sub-anchored subtrees (a nearer
    /// anchor shadows this one for everything under it). In-memory walk over the tenants registry
    /// — tiny by construction (docs/26).
    /// </summary>
    public static async Task<IReadOnlyList<string>> CoveredTenantsAsync(
        DbContext db, string anchorId, CancellationToken ct)
    {
        var tenants = await db.Set<TenantEntity>().ToListAsync(ct);   // registry: not tenant-scoped
        var anchors = (await db.Set<SubscriptionEntity>().IgnoreQueryFilters()
            .Select(s => s.TenantId).ToListAsync(ct)).ToHashSet();
        return Covered(tenants, anchors, anchorId);
    }

    /// <summary>Sync twin for views.</summary>
    public static IReadOnlyList<string> CoveredTenants(DbContext db, string anchorId)
    {
        var tenants = db.Set<TenantEntity>().ToList();
        var anchors = db.Set<SubscriptionEntity>().IgnoreQueryFilters()
            .Select(s => s.TenantId).ToList().ToHashSet();
        return Covered(tenants, anchors, anchorId);
    }

    /// <summary>The root-to-self chain; a node without a registry row degrades to itself
    /// (the pre-hierarchy shape, docs/26).</summary>
    public static string[] Chain(TenantEntity? node, string tenantId) =>
        node?.AncestorIds().ToArray() ?? [tenantId];

    /// <summary>Nearest ancestor-or-self anchor; free default anchored at the ROOT otherwise.</summary>
    public static CoveringSubscription Pick(string[] chain, List<SubscriptionEntity> rows)
    {
        var byNode = rows.ToDictionary(r => r.TenantId);
        for (var i = chain.Length - 1; i >= 0; i--)
        {
            if (byNode.TryGetValue(chain[i], out var hit))
                return new CoveringSubscription(hit, chain[i]);
        }
        return new CoveringSubscription(new SubscriptionEntity
        {
            TenantId = chain[0],
            Plan = Defaults.Plan,
            Seats = Defaults.Seats,
            EntitlementsJson = System.Text.Json.JsonSerializer.Serialize(Defaults.Entitlements),
        }, chain[0]);
    }

    private static List<string> Covered(
        List<TenantEntity> tenants, HashSet<string> anchors, string anchorId)
    {
        var anchor = tenants.FirstOrDefault(t => t.Id == anchorId);
        if (anchor is null) return [anchorId];   // pre-hierarchy: the node covers itself

        var covered = new List<string>();
        foreach (var tenant in tenants
                     .Where(t => TenantEntity.IsSelfOrDescendant(anchor.Path, t.Path)))
        {
            // Shadowed iff a NEARER anchor sits strictly below ours on the tenant's chain.
            var chain = tenant.AncestorIds();
            var shadowed = false;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (chain[i] == anchorId) break;
                if (anchors.Contains(chain[i])) { shadowed = true; break; }
            }
            if (!shadowed) covered.Add(tenant.Id);
        }
        return covered;
    }
}
