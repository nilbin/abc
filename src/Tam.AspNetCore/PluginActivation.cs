using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// Wraps the tenant registry so plugin-packaged fields (docs/22 P2) join the effective overlay
/// exactly like tenant-defined fields — but only for tenants with the owning plugin active.
/// Everything downstream (forms, grids, audit, MCP, D7 filtering, the extension change channel)
/// already treats overlay specs uniformly, so packaged fields cost nothing new there.
/// </summary>
public sealed class PluginAwareExtensionRegistry(
    IExtensionRegistry inner, TamModel model, Microsoft.EntityFrameworkCore.DbContext db,
    IServiceProvider services) : IExtensionRegistry
{
    public async Task<IReadOnlyList<ExtensionFieldSpec>> For(
        TenantId tenant, string entityKey, CancellationToken ct)
    {
        var specs = await inner.For(tenant, entityKey, ct);
        var packaged = await PackagedFor(tenant, ct);
        return packaged.TryGetValue(entityKey, out var extra) ? [.. extra, .. specs] : specs;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ExtensionFieldSpec>>> All(
        TenantId tenant, CancellationToken ct)
    {
        var specs = await inner.All(tenant, ct);
        var packaged = await PackagedFor(tenant, ct);
        if (packaged.Count == 0) return specs;

        var merged = specs.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        foreach (var (entity, extras) in packaged)
        {
            if (!merged.TryGetValue(entity, out var list)) merged[entity] = list = [];
            list.InsertRange(0, extras);
        }
        return merged.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ExtensionFieldSpec>)kv.Value);
    }

    private async Task<Dictionary<string, List<ExtensionFieldSpec>>> PackagedFor(
        TenantId tenant, CancellationToken ct)
    {
        if (model.PackagedFields.Count == 0) return [];
        var active = await ActivationCache.ForAsync(services, db, tenant.Value, ct);
        return model.PackagedFields
            .Where(f => active.Contains(f.PluginId))
            .GroupBy(f => f.EntityKey)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Spec).ToList());
    }
}

/// <summary>Per-tenant plugin activation state (docs/22): compiled code, tenant data switch.</summary>
public static class PluginActivations
{
    public static async Task<IReadOnlySet<string>> ActiveAsync(
        DbContext db, string tenantId, CancellationToken ct)
    {
        // Runs in request AND background scopes and filters by an explicit tenantId, so it opts out
        // of the ambient global filter (which would return nothing when no request tenant is set).
        var active = await db.Set<PluginActivationEntity>()
            .AcrossTenants()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.PluginId)
            .ToListAsync(ct);
        return active.ToHashSet();
    }
}

/// <summary>
/// Request-scoped memoization of the activation set. The set is read up to 3-4 times per
/// operation/view request (existence check, gate stage, extension overlay, manifest) — caching
/// it per (scope, tenant) collapses those to one query AND removes the incoherency window where
/// a concurrent deactivate could be seen by one read and not another in the same request.
/// </summary>
public sealed class ActivationCache(ITamDb tam, TamModel model)
{
    private readonly Dictionary<string, IReadOnlySet<string>> byTenant = new();

    public async ValueTask<IReadOnlySet<string>> ActiveAsync(string tenantId, CancellationToken ct)
    {
        if (byTenant.TryGetValue(tenantId, out var set)) return set;
        // Framework packages are ALWAYS active — no row, no toggle (docs/22 package tier).
        var stored = await PluginActivations.ActiveAsync(tam.Db, tenantId, ct);
        set = stored.Union(model.Packages.Keys).ToHashSet();
        byTenant[tenantId] = set;
        return set;
    }

    /// <summary>Resolve the request cache if present, else fall back to a direct query (background
    /// services outside a request scope pass their own DbContext).</summary>
    public static async ValueTask<IReadOnlySet<string>> ForAsync(
        IServiceProvider services, DbContext db, string tenantId, CancellationToken ct)
    {
        if (services.GetService(typeof(ActivationCache)) is ActivationCache cache)
            return await cache.ActiveAsync(tenantId, ct);
        var stored = await PluginActivations.ActiveAsync(db, tenantId, ct);
        return services.GetService(typeof(TamModel)) is TamModel model
            ? stored.Union(model.Packages.Keys).ToHashSet()
            : stored;
    }

    /// <summary>THE docs/22 existence rule, in one place: a contribution from an inactive
    /// plugin does not exist for the tenant — host contributions (null plugin) always exist,
    /// framework packages are always active. Every endpoint and executor asks HERE.</summary>
    public static async ValueTask<bool> ContributionExistsAsync(
        IServiceProvider services, DbContext db, string? pluginId, string tenantId, CancellationToken ct)
    {
        if (pluginId is null) return true;
        var active = await ForAsync(services, db, tenantId, ct);
        return active.Contains(pluginId);
    }
}
