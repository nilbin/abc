using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Plugin activation admin (docs/22). Wears the package label for uniformity, but its
/// always-on status is non-negotiable — who activates the activator.</summary>
[TamPackage("tam.plugins", "plugins", "web.plugins")]
public sealed class TamPluginsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // Nav CONTENT + suggestion (docs/30 D-N2) — the host owns placement.
        plugin.Nav(nav => nav.Page("plugins", grid: "web.plugins", suggest: "administration", order: 40));
        plugin
            .AddOperationType(typeof(ActivatePlugin))
            .AddOperationType(typeof(DeactivatePlugin))
            .AddViewType(typeof(PluginList))
            .Grid<PluginList.Result>("web.plugins", "plugins.list", grid =>
            {
                grid.Column(x => x.PluginId);
                grid.Column(x => x.Active);
                grid.RowAction("plugins.activate");
                grid.RowAction("plugins.deactivate");
            });
    }
}

public static class PluginFindings
{
    public static readonly FindingFactory Unknown = Finding.Error("plugins.unknown");

    /// <summary>docs/37 D-V4, level-1 activation guard: a plugin is activatable only where the
    /// plugins it DependsOn are already active.</summary>
    public static readonly FindingFactory DependencyInactive = Finding.Error("plugins.dependency-inactive");

    /// <summary>The symmetric guard that keeps the invariant true continuously: a plugin cannot
    /// be deactivated while an active plugin still depends on it (cascade/suspend is the deferred
    /// heavy machinery — this just refuses the incoherent state).</summary>
    public static readonly FindingFactory DependentActive = Finding.Error("plugins.dependent-active");
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

        // Entitlement gate (docs/24): the plan must include this plugin. A localized upsell,
        // not a crash — and a tenant with no subscription is the free plan (no entitlements).
        // Entitlement is the COVERING anchor's (docs/24 hierarchy): the money cascades down
        // the tree; activation stays this node's choice.
        var covering = await Subscriptions.CoveringAsync(tam.Db, context.TenantId.Value, ct);
        if (!covering.Subscription.Entitles(input.PluginId))
            return SubscriptionFindings.NotEntitled.With(("plugin", input.PluginId)).At(nameof(Input.PluginId));

        // Dependency guard (docs/37 D-V4, L1): a plugin activates only where its declared
        // parents are already active. PLG011 guarantees every edge targets a registered plugin,
        // so only plugin activation rows need consulting (packages are always-on).
        var dependsOn = model.Plugins[input.PluginId].DependsOn;
        if (dependsOn.Count > 0)
        {
            var active = (await tam.Db.Set<PluginActivationEntity>()
                .Select(x => x.PluginId).ToListAsync(ct)).ToHashSet();
            var missing = dependsOn.FirstOrDefault(d => !active.Contains(d));
            if (missing is not null)
                return PluginFindings.DependencyInactive
                    .With(("plugin", input.PluginId), ("dependency", missing)).At(nameof(Input.PluginId));
        }

        var existing = await tam.Db.Set<PluginActivationEntity>().SingleOrDefaultAsync(
            x => x.PluginId == input.PluginId, ct);
        if (existing is null)
        {
            tam.Db.Add(new PluginActivationEntity
            {
                Id = Guid.NewGuid(),
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

        // Dependent guard (docs/37 D-V4): keep the L1 invariant true continuously — refuse to
        // pull a plugin out from under an active dependent. Cascade/suspend is deferred; this
        // just refuses the incoherent state, so the admin deactivates top-down.
        var dependents = model.Plugins.Values
            .Where(p => p.DependsOn.Contains(input.PluginId)).Select(p => p.Id).ToList();
        if (dependents.Count > 0)
        {
            var active = (await tam.Db.Set<PluginActivationEntity>()
                .Select(x => x.PluginId).ToListAsync(ct)).ToHashSet();
            var blocker = dependents.FirstOrDefault(active.Contains);
            if (blocker is not null)
                return PluginFindings.DependentActive
                    .With(("plugin", input.PluginId), ("dependent", blocker)).At(nameof(Input.PluginId));
        }

        var existing = await tam.Db.Set<PluginActivationEntity>().SingleOrDefaultAsync(
            x => x.PluginId == input.PluginId, ct);
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
        public bool Active { get; init; }
    }

    public static IQueryable<Result> Execute(
        Query query, OperationContext context, ITamDb tam, TamModel model)
    {
        // The compiled plugin list is model data; only the activation state lives in the
        // database. Small by construction, so an in-memory source is the honest shape here.
        var active = tam.Db.Set<PluginActivationEntity>()
            .Select(x => x.PluginId)
            .ToHashSet();

        return model.Plugins.Keys.Order()
            .Where(id => string.IsNullOrWhiteSpace(query.Search) || id.Contains(query.Search!))
            .Select(id => new Result { PluginId = id, Active = active.Contains(id) })
            .AsQueryable();
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.PluginId)).DefaultSort(nameof(Result.PluginId));
}
