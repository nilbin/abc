using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// Dependency-free PBKDF2 password hashing (SHA-256, 100k iterations, per-hash salt).
/// Format: "pbkdf2${iterations}${salt-b64}${hash-b64}" — versionable by prefix.
/// </summary>
public static class TamPasswords
{
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
/// Actor resolution (docs/26 + docs/27): the "tam:account" claim names the global account; grants at
/// the ACTIVE node are the union up its ancestor chain — the active node's own membership contributes
/// every role assignment, ancestor memberships contribute only the CASCADING ones (D-H5), and each
/// membership's role names resolve against its OWN node's role definitions, never the active node's
/// (docs/27 — cross-level role resolution). Everything is re-read per request, so a revoked role
/// or cascade takes effect immediately. No membership on the chain ⇒ no grants — the cross-tenant guard:
/// a token for account X speaks for tenant Y only if X is a member of Y or of a cascading ancestor.
/// Grants never flow up. Replace IActorProvider for any other authentication mechanism.
/// </summary>
public sealed class ClaimsActorProvider : IActorProvider
{
    /// <summary>The token subject: the global account id (Guid). Set at grant time in SignIn.</summary>
    public const string AccountClaim = "tam:account";

    /// <summary>The account's chosen active tenant for this token (docs/26): names the tenant whose
    /// data the request sees, read by <see cref="ClaimTenantProvider"/>. Access to it is still proven
    /// by the account's membership, checked per request — the claim selects context, not access.</summary>
    public const string ActiveTenantClaim = "tam:tenant";

    public Actor GetActor(HttpContext http)
    {
        var accountClaim = http.User.FindFirst(AccountClaim)?.Value;
        if (http.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(accountClaim, out var accountId))
            return Anonymous;

        var db = http.RequestServices.GetRequiredService<ITamDb>().Db;

        // The account is global (not tenant-scoped), so this lookup isn't filtered.
        var account = db.Set<AccountEntity>().FirstOrDefault(a => a.Id == accountId && a.Active);
        if (account is null) return Anonymous;

        // The active node and its ancestor chain, from the materialized path. An active node with no
        // TenantEntity row degrades to single-node behavior (the pre-hierarchy shape). Resolved via
        // the pinned ambient scope, so an act-as rebind (D-H4) yields Cap_eff(target) automatically.
        var activeId = TamTenant.Resolve(http).Value;
        var activeNode = db.Set<TenantEntity>().FirstOrDefault(t => t.Id == activeId);
        var chainIds = (activeNode?.AncestorIds() ?? [activeId]).ToArray();

        // Memberships along the chain are cross-tenant by nature — deliberate filter opt-out.
        var memberships = db.Set<TenantMembershipEntity>().IgnoreQueryFilters()
            .Where(m => m.AccountId == accountId && m.Active && chainIds.Contains(m.TenantId))
            .ToList();

        // Role names bind to the MEMBERSHIP's node definitions (docs/27): load the chain's rows
        // unfiltered once, then match per (tenant, name) — names never merge across levels. A role's grants are its explicit atoms plus its access levels expanded at load
        // time (docs/27 D-A1), so a new action on a resource flows into existing Manage roles
        // without a role edit.
        var model = http.RequestServices.GetRequiredService<TamModel>();
        var roleRows = db.Set<RoleEntity>().IgnoreQueryFilters()
            .Where(r => chainIds.Contains(r.TenantId))
            .ToList();
        var byNodeAndName = roleRows.ToDictionary(r => (r.TenantId, r.Name));

        // Plain union across memberships (docs/28: the actor is a flat grant set, and this union
        // is THE extension seam — groups, if ever, arrive as one more source flattened here).
        var grants = new HashSet<string>();
        foreach (var membership in memberships)
        {
            // Active node's membership contributes ALL assignments; ancestors only cascading (D-H5).
            var atActiveNode = membership.TenantId == activeId;
            foreach (var grant in membership.Roles()
                .Where(a => atActiveNode || a.Cascade)
                .Select(a => byNodeAndName.GetValueOrDefault((membership.TenantId, a.Name)))
                .Where(role => role is not null)
                .SelectMany(role => role!.Permissions().Concat(
                    role.Levels().SelectMany(l => AccessLevels.Expand(model, l.Key, l.Value)))))
                grants.Add(grant);
        }

        return new Actor(account.Id.ToString(), account.DisplayName, grants);
    }

    private static Actor Anonymous => new("anonymous", "Anonymous", new HashSet<string>());
}
