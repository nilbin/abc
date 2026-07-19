using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Tam.EntityFrameworkCore;

/// <summary>
/// Marks an entity as belonging to a tenant. The row-level tenant boundary is then enforced in ONE
/// place — an EF global query filter applied to every <see cref="ITenantScoped"/> type — instead of
/// a hand-written <c>Where(x =&gt; x.TenantId == …)</c> repeated at every call site, where one omission
/// is a silent cross-tenant leak. Background jobs that legitimately span tenants opt out explicitly
/// with <c>IgnoreQueryFilters()</c>.
/// </summary>
public interface ITenantScoped
{
    string TenantId { get; }
}

/// <summary>Carries the current request's tenant to the model's global query filter.</summary>
public interface ITenantScopeContext
{
    /// <summary>The tenant every query is scoped to, or null in a cross-tenant background scope
    /// (where callers must <c>IgnoreQueryFilters()</c> and filter by row explicitly).</summary>
    string? CurrentTenantId { get; }

    /// <summary>Additional tenants this request may READ (docs/26 D-H1: subtree views): the
    /// pipeline widens this to the acting node's validated subtree for a SubtreeRead view and
    /// nothing else. Empty everywhere else — including every write path: the stamp interceptor
    /// and operation pipeline use <see cref="CurrentTenantId"/> alone (D-H4).</summary>
    IReadOnlyList<string> TenantReadSet { get; }

    /// <summary>The acting node's PATH when the read set is a validated SUBTREE (the only
    /// widening the framework performs) — lets the RLS backstop answer subtree membership as
    /// a registry semi-join instead of a per-row array scan (docs/33, measured: 240 ms → 0.2 ms
    /// on a 200-node subtree over 20k rows). Null when no widening is active.</summary>
    string? TenantReadPath => null;

    /// <summary>True when this scope has been EXPLICITLY escalated to cross-tenant (docs/33
    /// D-R8: the auth branch, registry-mutating framework operations). The RLS backstop maps
    /// it to the '*' sentinel; hosts without RLS can ignore it — the EF filter never reads it
    /// (cross-tenant queries still opt out per query with <see cref="TamTenantFilter.AcrossTenants"/>).</summary>
    bool CrossTenantScope => false;

    /// <summary>True while the operation's derivations run (docs/40): the write-guard interceptor
    /// rejects any write command on this context so a derivation cannot cause a durable side effect.
    /// Defaults false; a host DbContext forwards it from its scoped tenant state.</summary>
    bool DerivationReadOnly => false;
}

public static class TamTenantFilter
{
    /// <summary>The query tag carried by every sanctioned cross-tenant READ (docs/33 D-R7).
    /// EF renders it as a leading SQL comment, which the RLS interceptor recognizes and runs
    /// that one command under the cross-tenant sentinel — the database-visible half of the
    /// IgnoreQueryFilters-plus-explicit-row-filter pattern.</summary>
    public const string CrossTenantQueryTag = "tam:cross-tenant";

    /// <summary>
    /// THE sanctioned cross-tenant read: <c>IgnoreQueryFilters</c> (the EF opt-out) plus the
    /// <see cref="CrossTenantQueryTag"/> (the database opt-out, docs/33). Every framework call
    /// site that deliberately reads across tenants uses this — never raw IgnoreQueryFilters —
    /// so the two layers opt out TOGETHER and the pairing is greppable. Callers still filter
    /// rows explicitly; the composition rule (docs/27) is unchanged.
    /// </summary>
    public static IQueryable<T> AcrossTenants<T>(this IQueryable<T> source) where T : class =>
        source.IgnoreQueryFilters().TagWith(CrossTenantQueryTag);

    /// <summary>
    /// Applies the tenant global query filter to every <see cref="ITenantScoped"/> entity in the
    /// model, comparing each row's TenantId to the context's <see cref="ITenantScopeContext.CurrentTenantId"/>.
    /// EF re-evaluates that context member per query, so one convention covers every current and
    /// future tenant-scoped table. Call from the app's <c>OnModelCreating</c> after mapping, passing
    /// the DbContext (which must implement <see cref="ITenantScopeContext"/>).
    /// </summary>
    public static void ApplyTenantFilter<TContext>(this ModelBuilder modelBuilder, TContext context)
        where TContext : DbContext, ITenantScopeContext
    {
        // context.CurrentTenantId, rooted at the DbContext so EF parameterizes it per execution.
        var currentTenant = Expression.Property(
            Expression.Constant(context), nameof(ITenantScopeContext.CurrentTenantId));
        var readSet = Expression.Property(
            Expression.Constant(context), nameof(ITenantScopeContext.TenantReadSet));
        var contains = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(string));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType)) continue;

            var e = Expression.Parameter(entityType.ClrType, "e");
            var tenantId = Expression.Property(e, nameof(ITenantScoped.TenantId));
            // e => e.TenantId == ctx.CurrentTenantId || ctx.TenantReadSet.Contains(e.TenantId)
            // A null current tenant matches nothing, so an unset (cross-tenant) scope sees no
            // rows unless it opts out. The read set is empty except during a SubtreeRead view,
            // where it is the acting node's validated subtree — reads widen, writes never do.
            var body = Expression.OrElse(
                Expression.Equal(tenantId, currentTenant),
                Expression.Call(contains, readSet, tenantId));
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, e));
        }
    }
}
