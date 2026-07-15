using Microsoft.EntityFrameworkCore;

namespace Tam.EntityFrameworkCore;

/// <summary>
/// The opt-in hierarchy read scopes (docs/26 D-H1 + docs/27 Axis 2). The global query filter stays
/// STRICT — one node — and a view widens explicitly by calling one of these; silent roll-up is a
/// data-exposure footgun, so breadth is always a visible choice in the view's own query. Both
/// resolve tenant sets through the (tiny) tenants table; rows carry only TenantId and no path is
/// denormalized onto them, which keeps re-parenting a tenants-table-only rewrite (docs/27
/// implementation notes).
///
/// ⚠ COMPOSITION RULE: EF's IgnoreQueryFilters is QUERY-WIDE, not per-source. Composing a widened
/// source into a query (a join, a subquery over another ITenantScoped set) strips the global strict
/// filter from EVERY source in that query — silently returning other tenants' rows. Therefore, in
/// any query that touches a widened source, every OTHER ITenantScoped source must be explicitly
/// scoped too: <see cref="InNode{T}"/> for strict, or its own InSubtree/WithInherited. A view that
/// uses exactly one ITenantScoped source is unaffected.
/// </summary>
public static class TamTreeScopes
{
    /// <summary>
    /// strict, made EXPLICIT: the same single-node scope the global filter gives implicitly, for
    /// composing with a widened source in the same query (see the composition rule above) — the
    /// query-wide IgnoreQueryFilters would otherwise strip this source's implicit filter.
    /// </summary>
    public static IQueryable<T> InNode<T>(this IQueryable<T> source, TenantId active)
        where T : class, ITenantScoped
        => source.IgnoreQueryFilters().Where(e => e.TenantId == active.Value);
    /// <summary>
    /// subtree (downward roll-up): rows owned at or BELOW the active node — the region manager's
    /// dashboard over all its companies. Semi-join against the tenants table on a segment-safe
    /// path prefix ("demo" is not an ancestor of "demo2").
    /// </summary>
    public static IQueryable<T> InSubtree<T>(this IQueryable<T> source, DbContext db, TenantId active)
        where T : class, ITenantScoped
    {
        var activePath = PathOf(db, active);
        var prefix = activePath + ".";
        var nodeIds = db.Set<TenantEntity>()
            .Where(t => t.Path == activePath || t.Path.StartsWith(prefix))
            .Select(t => t.Id);
        return source.IgnoreQueryFilters().Where(e => nodeIds.Contains(e.TenantId));
    }

    /// <summary>
    /// inherited (upward shared read): rows owned at or ABOVE the active node — reference data owned
    /// high and read by every node below (a group price list, a master customer registry). The
    /// ancestor set comes straight off the active node's own path, so this is a bounded IN-list; it
    /// can only ever expose the node's own ancestors, never a sibling subtree.
    /// </summary>
    public static IQueryable<T> WithInherited<T>(this IQueryable<T> source, DbContext db, TenantId active)
        where T : class, ITenantScoped
    {
        var ancestorIds = PathOf(db, active).Split('.');
        return source.IgnoreQueryFilters().Where(e => ancestorIds.Contains(e.TenantId));
    }

    /// <summary>An active node without a TenantEntity row degrades to itself (single-node behavior).</summary>
    private static string PathOf(DbContext db, TenantId active) =>
        db.Set<TenantEntity>().Where(t => t.Id == active.Value).Select(t => t.Path).FirstOrDefault()
            ?? active.Value;
}
