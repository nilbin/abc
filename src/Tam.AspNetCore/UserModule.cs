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
    public static readonly FindingFactory UnknownPolicy = Finding.Error("users.unknown-policy");
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
        [property: LabelKey("labels.roles")] List<string> Roles,
        [property: LabelKey("labels.policies")] List<string>? Policies = null);

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

        // Access policies (docs/27 Axis 2) are validated against the registry like roles are.
        if (input.Policies is { Count: > 0 })
        {
            var knownPolicies = await tam.Db.Set<AccessPolicyEntity>()
                .Select(x => x.Name).ToListAsync(ct);
            var unknownPolicies = input.Policies.Where(x => !knownPolicies.Contains(x)).ToList();
            if (unknownPolicies.Count > 0)
            {
                return new Result<Output>
                {
                    Findings = unknownPolicies.Select(x =>
                        UserFindings.UnknownPolicy.With(("policy", x)).At(nameof(Input.Policies))).ToList(),
                };
            }
        }

        var (account, membership) = await UserLookup.FindAccountAndMembershipAsync(
            tam, input.UserName, ct);

        if (membership is null || !membership.Active)
        {
            // Seat gate (docs/24): this define turns a non-active membership ACTIVE — brand new or
            // reactivated alike — so it consumes a seat; editing an already-active member does not.
            // (Gating only brand-new rows would let deactivate/reactivate churn breach the ceiling.)
            // Over the plan's ceiling → a localized upsell.
            var subscription = await Subscriptions.ForAsync(tam.Db, context.TenantId.Value, ct);
            var activeMembers = await tam.Db.Set<TenantMembershipEntity>()
                .CountAsync(m => m.Active, ct);
            if (activeMembers >= subscription.Seats)
                return SubscriptionFindings.SeatLimit
                    .With(("seats", subscription.Seats)).At(nameof(Input.UserName));

            // Seat lease: consuming a seat writes the subscription row, so two defines racing past
            // the count above conflict at SaveChanges (version token / duplicate key) instead of
            // both slipping under the ceiling. The free default has no row yet — materialize it.
            if (tam.Db.Entry(subscription).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
                tam.Db.Add(subscription);
            else
                subscription.Version++;
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
        membership.PoliciesJson = System.Text.Json.JsonSerializer.Serialize(input.Policies ?? []);
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
