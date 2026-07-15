using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The request's ambient tenant, feeding the EF global query filter (see <see cref="ITenantScoped"/>).
/// Scoped: set once per request from <see cref="ITenantProvider"/> — possibly REBOUND to a validated
/// act-as target (docs/26 D-H4: cross-node create) — and read by the DbContext's filter, the actor
/// resolver and every endpoint. Null outside a request (background jobs, startup seed), where callers
/// use IgnoreQueryFilters.
/// </summary>
public sealed class TenantScope
{
    public string? Current { get; set; }
}

public static class TamTenant
{
    /// <summary>
    /// The ONE ambient-tenant read: the pinned (possibly act-as-rebound) <see cref="TenantScope"/>,
    /// falling back to the provider when no scope was pinned (e.g. a host without the middleware).
    /// Everything that needs "the request's tenant" — context building, actor resolution, SSE
    /// subscription — goes through here so an act-as rebind is honored everywhere at once.
    /// </summary>
    public static TenantId Resolve(HttpContext http)
    {
        var scoped = http.RequestServices.GetService<TenantScope>()?.Current;
        return scoped is { Length: > 0 }
            ? new TenantId(scoped)
            : http.RequestServices.GetRequiredService<ITenantProvider>().GetTenant(http);
    }
}

/// <summary>Hierarchy membership questions shared by the auth server and the act-as middleware.</summary>
public static class TenantTree
{
    /// <summary>
    /// May this account STAND at (act in) <paramref name="tenantId"/>? True for a node the account
    /// has an active membership in, and for any descendant of a membership carrying at least one
    /// CASCADING role (docs/26 D-H3/D-H5) — the region admin acts in a sub-company with no
    /// membership row there. Grants never flow up, so an ancestor of a membership is NOT standable.
    /// </summary>
    public static bool IsStandable(DbContext db, Guid accountId, string tenantId)
    {
        // Cross-tenant by nature — deliberate filter opt-out.
        var memberships = db.Set<TenantMembershipEntity>().IgnoreQueryFilters()
            .Where(m => m.AccountId == accountId && m.Active)
            .ToList();
        var memberNodeIds = memberships.Select(m => m.TenantId).ToHashSet();
        if (memberNodeIds.Contains(tenantId)) return true;

        var target = db.Set<TenantEntity>().FirstOrDefault(t => t.Id == tenantId);
        if (target is null) return false;

        var cascadingRoots = memberships
            .Where(m => m.Roles().Any(a => a.Cascade))
            .Select(m => m.TenantId)
            .ToHashSet();
        if (cascadingRoots.Count == 0) return false;

        var rootPaths = db.Set<TenantEntity>()
            .Where(t => cascadingRoots.Contains(t.Id))
            .Select(t => t.Path)
            .ToList();
        return rootPaths.Any(p => TenantEntity.IsSelfOrDescendant(p, target.Path));
    }
}

public static class TenantScopeMiddleware
{
    /// <summary>The act-as header (docs/26 D-H4): names a target node for THIS request, validated
    /// against the account's standable set, then pinned as the ambient tenant — so the row stamp,
    /// audit, outbox/effects, idempotency and lookups all land coherently in the target.</summary>
    public const string ActAsHeader = "X-Tam-Tenant";

    /// <summary>
    /// Resolves the tenant for each request and pins it into the ambient <see cref="TenantScope"/>
    /// BEFORE anything touches the database — actor/role resolution already runs tenant-filtered,
    /// so this must sit ahead of it in the pipeline (and after authentication, so claim-based
    /// providers see the principal). An <see cref="ActAsHeader"/> rebinds the ambient tenant to a
    /// validated target node; an unauthorized target is refused outright (403) rather than silently
    /// falling back — a fallback would land the write in the wrong tenant.
    /// </summary>
    public static IApplicationBuilder UseTamTenantScope(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var ambient = http.RequestServices.GetRequiredService<ITenantProvider>().GetTenant(http).Value;

            var actAs = http.Request.Headers[TenantScopeMiddleware.ActAsHeader].ToString();
            if (actAs.Length > 0 && actAs != ambient)
            {
                var accountClaim = http.User.FindFirst(ClaimsActorProvider.AccountClaim)?.Value;
                var allowed = Guid.TryParse(accountClaim, out var accountId)
                    && TenantTree.IsStandable(
                        http.RequestServices.GetRequiredService<ITamDb>().Db, accountId, actAs);
                if (!allowed)
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new
                    {
                        findings = new[] { new { code = "tenants.not-standable", severity = "error" } },
                    });
                    return;
                }
                ambient = actAs;
            }

            http.RequestServices.GetRequiredService<TenantScope>().Current = ambient;
            await next(http);
        });
}
