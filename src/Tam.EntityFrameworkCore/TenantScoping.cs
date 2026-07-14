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
}

public static class TamTenantFilter
{
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

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType)) continue;

            var e = Expression.Parameter(entityType.ClrType, "e");
            var tenantId = Expression.Property(e, nameof(ITenantScoped.TenantId));
            // e => e.TenantId == context.CurrentTenantId  — a null current tenant matches nothing,
            // so an unset (cross-tenant) scope sees no rows unless it opts out of the filter.
            var body = Expression.Equal(tenantId, currentTenant);
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, e));
        }
    }
}
