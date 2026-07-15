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

    /// <summary>Extra tenants this request may READ (docs/26 D-H1). Set ONLY by the view
    /// executor for a SubtreeRead view — the acting node's validated subtree; empty otherwise.
    /// The write side (stamping, operations, outbox) never consults it.</summary>
    public IReadOnlyList<string> ReadSet { get; private set; } = [];

    public void WidenRead(IEnumerable<string> tenantIds) => ReadSet = [.. tenantIds];
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

            // Header first; query-string fallback for transports that cannot set headers (the SSE
            // EventSource) — same validation either way.
            var actAs = http.Request.Headers[TenantScopeMiddleware.ActAsHeader].ToString();
            if (actAs.Length == 0) actAs = http.Request.Query["actAs"].ToString();
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
