using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static class UserFindings
{
    public static readonly FindingFactory UnknownRole = Finding.Error("users.unknown-role");
    public static readonly FindingFactory InvalidName = Finding.Error("users.invalid-name");
}

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
/// (docs/27 — cross-level role resolution). Everything is re-read per request, so a revoked role or
/// cascade takes effect immediately. No membership on the chain ⇒ no grants — the cross-tenant guard:
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

        // Active node's membership contributes ALL assignments; ancestors only cascading ones (D-H5).
        var wanted = new List<(string TenantId, string Role)>();
        foreach (var membership in memberships)
        {
            var atActiveNode = membership.TenantId == activeId;
            foreach (var assignment in membership.Roles())
                if (atActiveNode || assignment.Cascade)
                    wanted.Add((membership.TenantId, assignment.Name));
        }
        if (wanted.Count == 0)
            return new Actor(account.Id.ToString(), account.DisplayName, new HashSet<string>());

        // Role names bind to the MEMBERSHIP's node definitions (docs/27): load the chain's role rows
        // unfiltered once, then match per (tenant, name) — names never merge across levels.
        var roleRows = db.Set<RoleEntity>().IgnoreQueryFilters()
            .Where(r => chainIds.Contains(r.TenantId))
            .ToList();
        var byNodeAndName = roleRows.ToDictionary(r => (r.TenantId, r.Name));
        var grants = wanted
            .Select(w => byNodeAndName.GetValueOrDefault((w.TenantId, w.Role)))
            .Where(role => role is not null)
            .SelectMany(role => role!.Permissions())
            .ToHashSet();

        return new Actor(account.Id.ToString(), account.DisplayName, grants);
    }

    private static Actor Anonymous => new("anonymous", "Anonymous", new HashSet<string>());
}

/// <summary>Users are tenant data managed through operations, like roles and custom fields (D1).</summary>
[Operation("users.define")]
[Authorize("users.manage")]
public static class DefineUser
{
    public sealed record Input(
        [property: LabelKey("labels.user-name")] string UserName,
        [property: LabelKey("labels.display-name")] string DisplayName,
        [property: LabelKey("labels.password")] string? Password,
        [property: LabelKey("labels.roles")] List<string> Roles);

    public sealed record Output(Guid UserId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.UserName, "^[a-z][a-z0-9.-]*$"))
            return UserFindings.InvalidName.At(nameof(Input.UserName));

        var knownRoles = await tam.Db.Set<RoleEntity>()
            .Select(x => x.Name)
            .ToListAsync(ct);
        var unknown = input.Roles.Where(r => !knownRoles.Contains(r)).ToList();
        if (unknown.Count > 0)
        {
            return new Result<Output>
            {
                Findings = unknown.Select(r =>
                    UserFindings.UnknownRole.With(("role", r)).At(nameof(Input.Roles))).ToList(),
            };
        }

        // A "user in this tenant" is a platform-global account (docs/26) plus its membership here.
        // The account is looked up globally by handle; the membership is tenant-scoped (global filter).
        var account = await tam.Db.Set<AccountEntity>().SingleOrDefaultAsync(
            a => a.Email == input.UserName, ct);
        var membership = account is null
            ? null
            : await tam.Db.Set<TenantMembershipEntity>()
                .SingleOrDefaultAsync(m => m.AccountId == account.Id, ct);

        if (membership is null)
        {
            // Seat gate (docs/24): a NEW membership in this tenant consumes a seat; reactivating or
            // editing an existing one does not. Over the plan's ceiling → a localized upsell.
            var subscription = await Subscriptions.ForAsync(tam.Db, context.TenantId.Value, ct);
            var activeMembers = await tam.Db.Set<TenantMembershipEntity>()
                .CountAsync(m => m.Active, ct);
            if (activeMembers >= subscription.Seats)
                return SubscriptionFindings.SeatLimit
                    .With(("seats", subscription.Seats)).At(nameof(Input.UserName));
        }

        if (account is null)
        {
            account = new AccountEntity { Id = Guid.NewGuid(), Email = input.UserName };
            tam.Db.Add(account);
        }
        account.DisplayName = input.DisplayName;
        if (input.Password is { Length: > 0 } password)
            account.PasswordHash = TamPasswords.Hash(password);
        account.Active = true;

        if (membership is null)
        {
            membership = new TenantMembershipEntity
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,   // explicit: seed/background inserts aren't stamped
                AccountId = account.Id,
            };
            tam.Db.Add(membership);
        }
        membership.RolesJson = System.Text.Json.JsonSerializer.Serialize(input.Roles);
        membership.Active = true;

        return new Output(account.Id);
    }
}

[Operation("users.deactivate")]
[Authorize("users.manage")]
public static class DeactivateUser
{
    public sealed record Input([property: LabelKey("labels.user-name")] string UserName);

    public sealed record Output(string UserName);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        // Deactivate the account's access to THIS tenant (its membership), not the global account —
        // the same person may still be active in other tenants (docs/26).
        var account = await tam.Db.Set<AccountEntity>().SingleOrDefaultAsync(
            a => a.Email == input.UserName, ct);
        var membership = account is null
            ? null
            : await tam.Db.Set<TenantMembershipEntity>()
                .SingleOrDefaultAsync(m => m.AccountId == account.Id, ct);
        if (membership is null) return PipelineFindings.NotFound.Create();

        membership.Active = false;   // revoke access here, never delete — the audit trail references the actor
        return new Output(input.UserName);
    }
}

[View("users.list")]
[Authorize("users.manage")]
public static class UserList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.user-name")]
        public string UserName { get; init; } = "";
        [LabelKey("labels.display-name")]
        public string DisplayName { get; init; } = "";
        [LabelKey("labels.roles")]
        public string Roles { get; init; } = "";
        [LabelKey("labels.active")]
        public bool Active { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        // A tenant's user list is its memberships (global filter scopes them to this tenant) joined
        // to the platform-global accounts they grant access to (docs/26).
        var members = tam.Db.Set<TenantMembershipEntity>();
        var accounts = tam.Db.Set<AccountEntity>();
        var rows = from m in members
                   join a in accounts on m.AccountId equals a.Id
                   select new { Account = a, Membership = m };
        if (!string.IsNullOrWhiteSpace(query.Search))
            rows = rows.Where(x => x.Account.Email.Contains(query.Search!));
        return rows.Select(x => new Result
        {
            Id = x.Account.Id, UserName = x.Account.Email, DisplayName = x.Account.DisplayName,
            Roles = x.Membership.RolesJson, Active = x.Membership.Active,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.UserName)).DefaultSort(nameof(Result.UserName));
}
