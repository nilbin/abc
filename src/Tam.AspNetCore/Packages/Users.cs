using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>User & invite admin (docs/26). Auth itself is unaffected by this surface — actor
/// resolution reads the identity tables directly; the invite ACCEPT page lives with the auth
/// server (Tam.Auth.OpenIddict).</summary>
[TamPackage("tam.users", "users", "web.users")]
public sealed class TamUsersPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // Nav CONTENT + suggestion (docs/30 D-N2) — the host owns placement.
        plugin.Nav(nav => nav.Page("users", grid: "web.users", suggest: "administration", order: 10));
        plugin
            .AddOperationType(typeof(DefineUser))
            .AddOperationType(typeof(InviteUser))
            .AddOperationType(typeof(DeactivateUser))
            .AddViewType(typeof(UserList))
            .AddViewType(typeof(UserDirectoryLookup))
            .Form<InviteUser.Input>("web.users.invite", "users.invite", form =>
            {
                form.Field(x => x.Email);
                form.Field(x => x.DisplayName);
                form.Field(x => x.Roles).Renderer("string-list");
            })
            .Grid<UserList.Result>("web.users", "users.list", grid =>
            {
                grid.Column(x => x.UserName);
                grid.Column(x => x.DisplayName);
                grid.Column(x => x.Roles);
                grid.Column(x => x.Active);
                grid.ToolbarAction("users.invite");
                grid.RowAction("users.deactivate");
            });
    }
}

public static class UserFindings
{
    public static readonly FindingFactory UnknownRole = Finding.Error("users.unknown-role");
    public static readonly FindingFactory InvalidName = Finding.Error("users.invalid-name");
}

internal static class MembershipRules
{
    /// <summary>Seat gate + lease (docs/24), shared by define and invite: turning a non-active
    /// membership ACTIVE consumes a seat — brand new or reactivated alike. Consuming writes the
    /// subscription row (IVersioned; the free default is materialized), so two consumers racing
    /// past the count conflict at SaveChanges instead of both slipping under the ceiling.</summary>
    public static async Task<Finding?> ConsumeSeatAsync(
        ITamDb tam, string tenantId, CancellationToken ct)
    {
        // Seats POOL at the covering anchor (docs/24 hierarchy): the count spans every node the
        // anchor covers, and the lease lands on the ANCHOR's row — two admins racing invites at
        // two different covered nodes now conflict at SaveChanges instead of each passing a
        // node-local check. A materialized free default lands at the ROOT, never at a child
        // (a child row would silently shadow any future root plan).
        var covering = await Subscriptions.CoveringAsync(tam.Db, tenantId, ct);
        var covered = await Subscriptions.CoveredTenantsAsync(tam.Db, covering.AnchorTenantId, ct);
        var activeMembers = await tam.Db.Set<TenantMembershipEntity>().AcrossTenants()
            .CountAsync(m => m.Active && covered.Contains(m.TenantId), ct);
        if (activeMembers >= covering.Subscription.Seats)
            return SubscriptionFindings.SeatLimit.With(("seats", covering.Subscription.Seats));

        if (tam.Db.Entry(covering.Subscription).State == EntityState.Detached)
            tam.Db.Add(covering.Subscription);
        else
            covering.Subscription.Version++;
        return null;
    }

    /// <summary>Role names resolve against the ACTIVE tenant's registry — the same validation
    /// for define and invite. Returns field-targeted findings, empty when valid.</summary>
    /// <summary>The trunk users.define and users.invite share: validate the role
    /// assignments, find-or-create the account and THIS tenant's membership — consuming a
    /// seat exactly when the membership is new or inactive — then stamp roles + active.
    /// Callers keep only their tails: define sets credentials, invite mails the token.</summary>
    public static async Task<(AccountEntity? Account, List<Finding> Findings)>
        EnsureActiveMembershipAsync(
            ITamDb tam, OperationContext context, string email, string displayName,
            List<string> roles, string onField, string rolesField, CancellationToken ct)
    {
        var invalid = await ValidateAssignmentsAsync(tam, roles, rolesField, ct);
        if (invalid.Count > 0) return (null, invalid);

        var (account, membership) = await UserLookup.FindAccountAndMembershipAsync(tam, email, ct);

        if (membership is null || !membership.Active)
        {
            if (await ConsumeSeatAsync(tam, context.TenantId.Value, ct) is { } over)
                return (null, [over.At(onField)]);
        }

        if (account is null)
        {
            account = new AccountEntity { Id = Guid.NewGuid(), Email = email, DisplayName = displayName };
            tam.Db.Add(account);
        }

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
        membership.RolesJson = System.Text.Json.JsonSerializer.Serialize(roles);
        membership.Active = true;
        return (account, []);
    }

    public static async Task<List<Finding>> ValidateAssignmentsAsync(
        ITamDb tam, List<string> roles, string rolesField, CancellationToken ct)
    {
        var knownRoles = await tam.Db.Set<RoleEntity>()
            .Where(x => !x.Retired).Select(x => x.Name).ToListAsync(ct);
        return roles.Where(r => !knownRoles.Contains(r)).Select(r =>
            UserFindings.UnknownRole.With(("role", r)).At(rolesField)).ToList();
    }
}

internal static class UserLookup
{
    /// <summary>The platform-global account by handle plus its membership in the ACTIVE tenant
    /// (global filter) — the one lookup both users.define and users.deactivate start from.</summary>
    public static async Task<(AccountEntity? Account, TenantMembershipEntity? Membership)>
        FindAccountAndMembershipAsync(ITamDb tam, string userName, CancellationToken ct)
    {
        var account = await tam.Db.Set<AccountEntity>().SingleOrDefaultAsync(
            a => a.Email == userName, ct);
        var membership = account is null
            ? null
            : await tam.Db.Set<TenantMembershipEntity>()
                .SingleOrDefaultAsync(m => m.AccountId == account.Id, ct);
        return (account, membership);
    }
}

/// <summary>Users are tenant data managed through operations, like roles and custom fields (D1).</summary>
[Operation("users.define")]
[Authorize("users.manage")]
public static class DefineUser
{
    public sealed record Input(
        string UserName,
        string DisplayName,
        string? Password,
        List<string> Roles);

    public sealed record Output(Guid UserId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.UserName, "^[a-z][a-z0-9.-]*$"))
            return UserFindings.InvalidName.At(nameof(Input.UserName));

        var (account, findings) = await MembershipRules.EnsureActiveMembershipAsync(
            tam, context, input.UserName, input.DisplayName, input.Roles,
            onField: nameof(Input.UserName), rolesField: nameof(Input.Roles), ct: ct);
        if (findings.Count > 0) return new Result<Output> { Findings = findings };

        account!.DisplayName = input.DisplayName;
        if (input.Password is { Length: > 0 } password)
            account.PasswordHash = TamPasswords.Hash(password);
        account.Active = true;

        return new Output(account.Id);
    }
}

/// <summary>
/// Invite by email (docs/26): the account and membership are created up front — the seat is
/// consumed at INVITE time, so the admin's count is predictable — but the account has no password
/// until the invitee follows the mailed link and sets one. Only the token's hash is stored; the
/// link is the secret. Inviting an account that already has a password (a member of another
/// tenant — platform-global identity) just adds the membership and mails a notification; no
/// token round-trip is needed.
/// </summary>
[Operation("users.invite")]
[Authorize("users.manage")]
public static class InviteUser
{
    public sealed record Input(
        [property: LabelKey("auth.email")] string Email,
        string DisplayName,
        List<string> Roles);

    public sealed record Output(Guid UserId, bool InviteSent);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model,
        ITamEmail email, IHttpContextAccessor http, CancellationToken ct)
    {
        // The handle doubles as the mail address here, so '@'/'+' are allowed (users.define's
        // stricter shape stays for handle-style names).
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Email, "^[a-z0-9][a-z0-9.@+-]*$"))
            return UserFindings.InvalidName.At(nameof(Input.Email));

        var (account, findings) = await MembershipRules.EnsureActiveMembershipAsync(
            tam, context, input.Email, input.DisplayName, input.Roles,
            onField: nameof(Input.Email), rolesField: nameof(Input.Roles), ct: ct);
        if (findings.Count > 0) return new Result<Output> { Findings = findings };

        if (account!.PasswordHash is { Length: > 0 })
        {
            // Existing credentialed account: membership added, nothing to accept.
            await email.SendAsync(account.Email,
                model.Locales.Localize("auth.added-subject", context.Culture),
                model.Locales.Localize("auth.added-body", context.Culture), ct);
            return new Output(account.Id, false);
        }

        // One pending invite per account: re-inviting rotates the token and extends the expiry.
        var invite = await tam.Db.Set<InviteEntity>()
            .FirstOrDefaultAsync(i => i.AccountId == account.Id && i.AcceptedAtIso == null, ct);
        if (invite is null)
        {
            invite = new InviteEntity
            {
                Id = Guid.NewGuid(), TenantId = context.TenantId.Value, AccountId = account.Id,
            };
            tam.Db.Add(invite);
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        invite.TokenHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        invite.ExpiresAtIso = IsoTime.From(DateTimeOffset.UtcNow.AddDays(7));

        var request = http.HttpContext?.Request;
        var origin = request is null ? "" : $"{request.Scheme}://{request.Host}";
        var link = $"{origin}/connect/invite?token={token}";
        await email.SendAsync(account.Email,
            model.Locales.Localize("auth.invite-subject", context.Culture),
            model.Locales.Localize("auth.invite-body", context.Culture,
                new Dictionary<string, object?> { ["link"] = link }), ct);
        return new Output(account.Id, true);
    }
}

[Operation("users.deactivate")]
[Authorize("users.manage")]
public static class DeactivateUser
{
    public sealed record Input(string UserName);

    public sealed record Output(string UserName);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        // Deactivate the account's access to THIS tenant (its membership), not the global account —
        // the same person may still be active in other tenants (docs/26).
        var (_, membership) = await UserLookup.FindAccountAndMembershipAsync(
            tam, input.UserName, ct);
        if (membership is null) return PipelineFindings.NotFound.Create();

        membership.Active = false;   // revoke access here, never delete — the audit trail references the actor
        return new Output(input.UserName);
    }
}

/// <summary>The tenant DIRECTORY (docs/34 M5): active members as picker options — the
/// lookup behind every [Lookup("users.lookup")] actor-reference field (assignees,
/// approvers). Deliberately its OWN low-sensitivity atom: display names only, so granting
/// a dispatcher the picker never grants the users ADMIN surface (users.manage).</summary>
[View("users.lookup")]
[Authorize("users.lookup")]
public static class UserDirectoryLookup
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        public string DisplayName { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var members = tam.Db.Set<TenantMembershipEntity>().Where(m => m.Active);
        var accounts = tam.Db.Set<AccountEntity>().Where(a => a.Active);
        var rows = from m in members
                   join a in accounts on m.AccountId equals a.Id
                   select new Result { Id = a.Id, DisplayName = a.DisplayName };
        if (!string.IsNullOrWhiteSpace(query.Search))
            rows = rows.Where(x => x.DisplayName.Contains(query.Search!));
        return rows;
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.DisplayName)).DefaultSort(nameof(Result.DisplayName));
}

[View("users.list")]
[Authorize("users.manage")]
public static class UserList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        public string UserName { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Roles { get; init; } = "";
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
