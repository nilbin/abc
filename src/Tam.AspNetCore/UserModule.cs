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

internal static class MembershipRules
{
    /// <summary>Seat gate + lease (docs/24), shared by define and invite: turning a non-active
    /// membership ACTIVE consumes a seat — brand new or reactivated alike. Consuming writes the
    /// subscription row (IVersioned; the free default is materialized), so two consumers racing
    /// past the count conflict at SaveChanges instead of both slipping under the ceiling.</summary>
    public static async Task<Finding?> ConsumeSeatAsync(
        ITamDb tam, string tenantId, CancellationToken ct)
    {
        var subscription = await Subscriptions.ForAsync(tam.Db, tenantId, ct);
        var activeMembers = await tam.Db.Set<TenantMembershipEntity>()
            .CountAsync(m => m.Active, ct);
        if (activeMembers >= subscription.Seats)
            return SubscriptionFindings.SeatLimit.With(("seats", subscription.Seats));

        if (tam.Db.Entry(subscription).State == EntityState.Detached)
            tam.Db.Add(subscription);
        else
            subscription.Version++;
        return null;
    }

    /// <summary>Role names resolve against the ACTIVE tenant's registry — the same validation
    /// for define and invite. Returns field-targeted findings, empty when valid.</summary>
    public static async Task<List<Finding>> ValidateAssignmentsAsync(
        ITamDb tam, List<string> roles, string rolesField, CancellationToken ct)
    {
        var knownRoles = await tam.Db.Set<RoleEntity>().Select(x => x.Name).ToListAsync(ct);
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

        var invalid = await MembershipRules.ValidateAssignmentsAsync(
            tam, input.Roles, nameof(Input.Roles), ct);
        if (invalid.Count > 0) return new Result<Output> { Findings = invalid };

        var (account, membership) = await UserLookup.FindAccountAndMembershipAsync(
            tam, input.UserName, ct);

        if (membership is null || !membership.Active)
        {
            if (await MembershipRules.ConsumeSeatAsync(tam, context.TenantId.Value, ct) is { } over)
                return over.At(nameof(Input.UserName));
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
        [property: LabelKey("labels.display-name")] string DisplayName,
        [property: LabelKey("labels.roles")] List<string> Roles);

    public sealed record Output(Guid UserId, bool InviteSent);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model,
        ITamEmail email, IHttpContextAccessor http, CancellationToken ct)
    {
        // The handle doubles as the mail address here, so '@'/'+' are allowed (users.define's
        // stricter shape stays for handle-style names).
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Email, "^[a-z0-9][a-z0-9.@+-]*$"))
            return UserFindings.InvalidName.At(nameof(Input.Email));

        var invalid = await MembershipRules.ValidateAssignmentsAsync(
            tam, input.Roles, nameof(Input.Roles), ct);
        if (invalid.Count > 0) return new Result<Output> { Findings = invalid };

        var (account, membership) = await UserLookup.FindAccountAndMembershipAsync(
            tam, input.Email, ct);

        if (membership is null || !membership.Active)
        {
            if (await MembershipRules.ConsumeSeatAsync(tam, context.TenantId.Value, ct) is { } over)
                return over.At(nameof(Input.Email));
        }

        if (account is null)
        {
            account = new AccountEntity
            {
                Id = Guid.NewGuid(), Email = input.Email, DisplayName = input.DisplayName,
            };
            tam.Db.Add(account);
        }

        if (membership is null)
        {
            membership = new TenantMembershipEntity
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                AccountId = account.Id,
            };
            tam.Db.Add(membership);
        }
        membership.RolesJson = System.Text.Json.JsonSerializer.Serialize(input.Roles);
        membership.Active = true;

        if (account.PasswordHash is { Length: > 0 })
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
    public sealed record Input([property: LabelKey("labels.user-name")] string UserName);

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
