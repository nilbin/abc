using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

// The pluggable seams (docs/29): who the request is (IActorProvider) and which tenant it
// acts in (ITenantProvider). Registration defaults live in TamServices.cs.

public interface IActorProvider
{
    Actor GetActor(HttpContext http);
}

public interface ITenantProvider
{
    TenantId GetTenant(HttpContext http);
}

/// <summary>Development default: a single actor with every permission. Replace per application.</summary>
public sealed class DevActorProvider : IActorProvider
{
    public Actor GetActor(HttpContext http) => new("dev", "Development User", new HashSet<string> { "*" });
}

public sealed class FixedTenantProvider(string tenant) : ITenantProvider
{
    public TenantId GetTenant(HttpContext http) => new(tenant);
}

/// <summary>
/// Resolves the request's active tenant from the bearer token's active-tenant claim (docs/26): a
/// PKCE token names the tenant the account chose at login, and the account's membership in it is
/// re-checked per request (<see cref="ClaimsActorProvider"/>) — so the claim selects context, it
/// doesn't grant access. Falls back to <paramref name="fallback"/> for unauthenticated requests
/// (the interactive login/token endpoints, static files) where no tenant is named yet.
/// </summary>
public sealed class ClaimTenantProvider(string fallback) : ITenantProvider
{
    public TenantId GetTenant(HttpContext http)
    {
        var tenant = http.User.FindFirst(ClaimsActorProvider.ActiveTenantClaim)?.Value;
        return new TenantId(string.IsNullOrEmpty(tenant) ? fallback : tenant);
    }
}

/// <summary>
/// Registry-backed actor resolution (decision D1): grants come from the roles table; only the
/// role-name source (header, JWT claim, session) and display naming are application decisions.
/// </summary>
public class RoleActorProvider(
    Func<HttpContext, string> roleName,
    Func<string, string>? displayName = null) : IActorProvider
{
    public Actor GetActor(HttpContext http)
    {
        var name = roleName(http);
        var db = http.RequestServices.GetRequiredService<ITamDb>().Db;

        // The global query filter already scopes the role lookup to the ambient tenant.
        var role = db.Set<RoleEntity>().FirstOrDefault(
            x => x.Name == name && !x.Retired);

        return new Actor(
            name,
            displayName?.Invoke(name) ?? name,
            role?.Permissions() ?? new HashSet<string>());
    }
}
