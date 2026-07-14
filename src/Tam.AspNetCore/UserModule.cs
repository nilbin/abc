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
/// Actor resolution from the authenticated ClaimsPrincipal: the "tam:user" claim names the
/// user record; grants resolve fresh from the user's roles each request, so a revoked role
/// takes effect immediately regardless of token lifetime. This is the seam for ANY
/// authentication mechanism that yields claims — the built-in OpenIddict server, an external
/// IdP, or a reverse proxy; replace IActorProvider entirely for anything else.
/// </summary>
public sealed class ClaimsActorProvider : IActorProvider
{
    public const string UserClaim = "tam:user";

    /// <summary>The tenant the token was minted for. The request's own tenant must match it, so a
    /// token issued at tenant A cannot be replayed against tenant B where a same-named user exists
    /// (host-based multi-tenancy). Set at grant time in the token server's SignIn.</summary>
    public const string TenantClaim = "tam:tenant";

    public Actor GetActor(HttpContext http)
    {
        var userName = http.User.FindFirst(UserClaim)?.Value
            ?? http.User.Identity?.Name;
        if (http.User.Identity?.IsAuthenticated != true || userName is null)
            return new Actor("anonymous", "Anonymous", new HashSet<string>());

        var tenant = http.RequestServices.GetRequiredService<ITenantProvider>().GetTenant(http);

        // Token/tenant binding: a token carries the tenant it was issued for; if the request
        // resolved to a different tenant, this token does not speak for it. Reject to anonymous.
        var tokenTenant = http.User.FindFirst(TenantClaim)?.Value;
        if (tokenTenant is null || tokenTenant != tenant.Value)
            return new Actor("anonymous", "Anonymous", new HashSet<string>());

        var db = http.RequestServices.GetRequiredService<ITamDb>().Db;

        var user = db.Set<TamUserEntity>().FirstOrDefault(
            x => x.UserName == userName && x.Active);
        if (user is null)
            return new Actor(userName, userName, new HashSet<string>());

        var roleNames = user.Roles();
        var grants = db.Set<RoleEntity>()
            .Where(x => roleNames.Contains(x.Name))
            .AsEnumerable()
            .SelectMany(x => x.Permissions())
            .ToHashSet();

        return new Actor(user.UserName, user.DisplayName, grants);
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

        var user = await tam.Db.Set<TamUserEntity>().SingleOrDefaultAsync(
            x => x.UserName == input.UserName, ct);
        if (user is null)
        {
            // Seat gate (docs/24): a NEW active user consumes a seat; reactivating or editing
            // an existing one does not. Over the plan's ceiling → a localized upsell.
            var subscription = await Subscriptions.ForAsync(tam.Db, context.TenantId.Value, ct);
            var activeUsers = await tam.Db.Set<TamUserEntity>()
                .CountAsync(x => x.Active, ct);
            if (activeUsers >= subscription.Seats)
                return SubscriptionFindings.SeatLimit
                    .With(("seats", subscription.Seats)).At(nameof(Input.UserName));

            user = new TamUserEntity
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                UserName = input.UserName,
            };
            tam.Db.Add(user);
        }
        user.DisplayName = input.DisplayName;
        user.RolesJson = System.Text.Json.JsonSerializer.Serialize(input.Roles);
        if (input.Password is { Length: > 0 } password)
            user.PasswordHash = TamPasswords.Hash(password);
        user.Active = true;

        return new Output(user.Id);
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
        var user = await tam.Db.Set<TamUserEntity>().SingleOrDefaultAsync(
            x => x.UserName == input.UserName, ct);
        if (user is null) return PipelineFindings.NotFound.Create();

        user.Active = false;   // deactivate, never delete — the audit trail references the actor
        return new Output(user.UserName);
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
        var users = tam.Db.Set<TamUserEntity>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
            users = users.Where(x => x.UserName.Contains(query.Search!));
        return users.Select(x => new Result
        {
            Id = x.Id, UserName = x.UserName, DisplayName = x.DisplayName,
            Roles = x.RolesJson, Active = x.Active,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.UserName)).DefaultSort(nameof(Result.UserName));
}
