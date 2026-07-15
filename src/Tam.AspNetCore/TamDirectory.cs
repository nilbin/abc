using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The sanctioned people-lookup seam for plugins (docs/22): actor id ↔ email over the framework's
/// identity tables, so a plugin that assigns or notifies people (most of them) never queries
/// <c>AccountEntity</c>/<c>TenantMembershipEntity</c> internals directly. Tenant-anchored: an
/// email resolves only to an account that can already ACT in the ambient tenant — plugins can
/// grant plugin-roles to existing members, never reach across the tenant boundary.
/// </summary>
public interface ITamDirectory
{
    /// <summary>The actor id (<see cref="Actor.Id"/> shape) of the active account with this
    /// email holding an active membership in the ambient tenant; null otherwise.</summary>
    Task<string?> ActorIdByEmailAsync(string email, CancellationToken ct);

    /// <summary>Email addresses of the given actors' ACTIVE accounts (deactivated ones drop
    /// out — a notification list, not an identity dump).</summary>
    Task<IReadOnlyList<string>> EmailsAsync(IReadOnlyCollection<string> actorIds, CancellationToken ct);
}

internal sealed class TamDirectory(ITamDb tam) : ITamDirectory
{
    public async Task<string?> ActorIdByEmailAsync(string email, CancellationToken ct)
    {
        // The account is platform-global (unfiltered); the tenant boundary is the MEMBERSHIP,
        // which the ambient global filter scopes (docs/26).
        var account = await tam.Db.Set<AccountEntity>()
            .FirstOrDefaultAsync(a => a.Email == email && a.Active, ct);
        if (account is null) return null;
        return await tam.Db.Set<TenantMembershipEntity>()
                .AnyAsync(m => m.AccountId == account.Id && m.Active, ct)
            ? account.Id.ToString()
            : null;
    }

    public async Task<IReadOnlyList<string>> EmailsAsync(
        IReadOnlyCollection<string> actorIds, CancellationToken ct)
    {
        var ids = actorIds.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToList();
        if (ids.Count == 0) return [];
        return await tam.Db.Set<AccountEntity>()
            .Where(a => ids.Contains(a.Id) && a.Active)
            .Select(a => a.Email)
            .ToListAsync(ct);
    }
}
