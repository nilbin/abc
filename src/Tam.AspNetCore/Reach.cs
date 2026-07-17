using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

// The reach seam's host half (docs/35): the resolver domains call with a STORED reference,
// and the framework providers over the facts the framework owns (docs/26 identity). Plugin
// providers register through PluginBuilder.ReachProvider and are activation-gated here.

/// <summary>
/// Resolves stored reach references (docs/35): parse → look the kind up in the compiled
/// model → gate plugin kinds on activation → construct the provider (ctor injection) →
/// delegate. Fail closed at every step: a malformed ref, an unknown kind, or a kind owned
/// by an inactive plugin is simply "not within" — stored ACL entries survive a plugin's
/// deactivation as inert data (D-R3).
/// </summary>
public sealed class ReachResolver(TamModel model, ITamDb tam, ITamActivator activator)
{
    public async Task<bool> WithinAsync(
        string reference, OperationContext context, CancellationToken ct)
    {
        if (!ReachRef.TryParse(reference, out var reach)) return false;
        if (!model.Reaches.TryGetValue(reach.Kind, out var definition)) return false;
        if (!await ActivationCache.ContributionExistsAsync(
                context.Services, tam.Db, definition.PluginId, context.TenantId.Value, ct))
            return false;
        return await Provider(definition).ContainsAsync(reach, context, ct);
    }

    /// <summary>Picker options for one kind — empty (never an error) when the kind is unknown
    /// or its plugin inactive, so an ACL editor renders exactly the referencable sets.</summary>
    public async Task<IReadOnlyList<ReachOption>> SearchAsync(
        string kind, string? search, OperationContext context, CancellationToken ct)
    {
        if (!model.Reaches.TryGetValue(kind, out var definition)) return [];
        if (!await ActivationCache.ContributionExistsAsync(
                context.Services, tam.Db, definition.PluginId, context.TenantId.Value, ct))
            return [];
        return await Provider(definition).SearchAsync(search, context, ct);
    }

    private IReachProvider Provider(ReachDefinition definition) =>
        (IReachProvider)activator.Create(definition.ProviderType);
}

/// <summary>`user:{accountId}` — one specific person (docs/35 D-R2). Containment is an id
/// compare against the acting actor; search offers the tenant's active members.</summary>
public sealed class UserReach(ITamDb tam) : IReachProvider
{
    public Task<bool> ContainsAsync(ReachRef reach, OperationContext context, CancellationToken ct) =>
        Task.FromResult(
            Guid.TryParse(reach.Id, out var referenced)
            && Guid.TryParse(context.Actor.Id, out var acting)
            && referenced == acting);

    public async Task<IReadOnlyList<ReachOption>> SearchAsync(
        string? search, OperationContext context, CancellationToken ct)
    {
        // The ambient filter scopes memberships to the acting tenant; accounts are the global
        // identity table joined for display (docs/26).
        var members = tam.Db.Set<TenantMembershipEntity>().Where(m => m.Active)
            .Join(tam.Db.Set<AccountEntity>().Where(a => a.Active),
                m => m.AccountId, a => a.Id, (m, a) => new { a.Id, a.DisplayName });
        if (!string.IsNullOrWhiteSpace(search))
            members = members.Where(x => x.DisplayName.Contains(search!));
        var rows = await members.OrderBy(x => x.DisplayName).Take(50).ToListAsync(ct);
        return rows.Select(x => new ReachOption(
            new ReachRef("user", x.Id.ToString()), x.DisplayName)).ToList();
    }
}

/// <summary>`role:{name}` — everyone holding the named role at the ACTING node. Node-local by
/// design (docs/35): whether a cascaded ancestor role should count is the docs/27 cross-level
/// name-resolution question, deferred. A retired role reaches nobody (retire-don't-drop:
/// grants stop applying, and so does reach).</summary>
public sealed class RoleReach(ITamDb tam) : IReachProvider
{
    public async Task<bool> ContainsAsync(
        ReachRef reach, OperationContext context, CancellationToken ct)
    {
        if (reach.Id is null || !Guid.TryParse(context.Actor.Id, out var accountId)) return false;
        var live = await tam.Db.Set<RoleEntity>()
            .AnyAsync(r => r.Name == reach.Id && !r.Retired, ct);
        if (!live) return false;
        var memberships = await tam.Db.Set<TenantMembershipEntity>()
            .Where(m => m.AccountId == accountId && m.Active).ToListAsync(ct);
        return memberships.Any(m => m.Roles().Any(r => r.Name == reach.Id));
    }

    public async Task<IReadOnlyList<ReachOption>> SearchAsync(
        string? search, OperationContext context, CancellationToken ct)
    {
        var roles = tam.Db.Set<RoleEntity>().Where(r => !r.Retired);
        if (!string.IsNullOrWhiteSpace(search))
            roles = roles.Where(r => r.Name.Contains(search!));
        var names = await roles.OrderBy(r => r.Name).Select(r => r.Name).Take(50).ToListAsync(ct);
        return names.Select(n => new ReachOption(new ReachRef("role", n), n)).ToList();
    }
}

/// <summary>`tenant` — every member of the acting node: acting at all implies an active
/// membership here, so containment is unconditional. The one option's label is the node's
/// display name (data, not a locale key).</summary>
public sealed class TenantReach(ITamDb tam) : IReachProvider
{
    public Task<bool> ContainsAsync(ReachRef reach, OperationContext context, CancellationToken ct) =>
        Task.FromResult(true);

    public async Task<IReadOnlyList<ReachOption>> SearchAsync(
        string? search, OperationContext context, CancellationToken ct)
    {
        var name = await tam.Db.Set<TenantEntity>()
            .Where(t => t.Id == context.TenantId.Value)
            .Select(t => t.DisplayName).SingleOrDefaultAsync(ct);
        return [new ReachOption(new ReachRef("tenant", null), name ?? context.TenantId.Value)];
    }
}
