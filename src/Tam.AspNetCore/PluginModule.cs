using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static class PluginFindings
{
    public static readonly FindingFactory Unknown = Finding.Error("plugins.unknown");
}

/// <summary>
/// Wraps the tenant registry so plugin-packaged fields (docs/22 P2) join the effective overlay
/// exactly like tenant-defined fields — but only for tenants with the owning plugin active.
/// Everything downstream (forms, grids, audit, MCP, D7 filtering, the extension change channel)
/// already treats overlay specs uniformly, so packaged fields cost nothing new there.
/// </summary>
public sealed class PluginAwareExtensionRegistry(
    IExtensionRegistry inner, TamModel model, Microsoft.EntityFrameworkCore.DbContext db) : IExtensionRegistry
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
        var active = await PluginActivations.ActiveAsync(db, tenant.Value, ct);
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
        var active = await db.Set<PluginActivationEntity>()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.PluginId)
            .ToListAsync(ct);
        return active.ToHashSet();
    }
}

/// <summary>
/// Activation is an audited framework operation like any other (docs/22): installing plugin
/// code is the vendor's deploy; enabling it is the tenant's click. Already-active is a no-op —
/// the operation states desired state, not a transition.
/// </summary>
[Operation("plugins.activate")]
[Authorize("plugins.manage")]
public static class ActivatePlugin
{
    public sealed record Input(string PluginId);

    public sealed record Output(string PluginId, bool Active);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        if (!model.Plugins.ContainsKey(input.PluginId))
            return PluginFindings.Unknown.With(("plugin", input.PluginId)).At(nameof(Input.PluginId));

        var existing = await tam.Db.Set<PluginActivationEntity>().SingleOrDefaultAsync(
            x => x.TenantId == context.TenantId.Value && x.PluginId == input.PluginId, ct);
        if (existing is null)
        {
            tam.Db.Add(new PluginActivationEntity
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                PluginId = input.PluginId,
            });
        }
        return new Output(input.PluginId, Active: true);
    }
}

[Operation("plugins.deactivate")]
[Authorize("plugins.manage")]
public static class DeactivatePlugin
{
    public sealed record Input(string PluginId);

    public sealed record Output(string PluginId, bool Active);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        if (!model.Plugins.ContainsKey(input.PluginId))
            return PluginFindings.Unknown.With(("plugin", input.PluginId)).At(nameof(Input.PluginId));

        var existing = await tam.Db.Set<PluginActivationEntity>().SingleOrDefaultAsync(
            x => x.TenantId == context.TenantId.Value && x.PluginId == input.PluginId, ct);
        if (existing is not null) tam.Db.Remove(existing);

        // Deactivation hides, never deletes: the plugin's data outlives the switch, exactly
        // like retired extension fields (docs/15's retire-don't-drop principle).
        return new Output(input.PluginId, Active: false);
    }
}

/// <summary>Compiled plugins joined with this tenant's activation rows — the admin surface.</summary>
[View("plugins.list")]
[Authorize("plugins.manage")]
public static class PluginList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        [LabelKey("labels.plugin")]
        public string PluginId { get; init; } = "";
        [LabelKey("labels.active")]
        public bool Active { get; init; }
    }

    public static IQueryable<Result> Execute(
        Query query, OperationContext context, ITamDb tam, TamModel model)
    {
        // The compiled plugin list is model data; only the activation state lives in the
        // database. Small by construction, so an in-memory source is the honest shape here.
        var active = tam.Db.Set<PluginActivationEntity>()
            .Where(x => x.TenantId == context.TenantId.Value)
            .Select(x => x.PluginId)
            .ToHashSet();

        return model.Plugins.Keys.Order()
            .Select(id => new Result { PluginId = id, Active = active.Contains(id) })
            .AsQueryable();
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.PluginId)).DefaultSort(nameof(Result.PluginId));
}
