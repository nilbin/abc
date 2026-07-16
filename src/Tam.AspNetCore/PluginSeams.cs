using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

// Cross-domain plugin runtime seams (docs/31): the packaged-field WRITER (D-X2) and the host
// view READER (D-X3). Both are plugin-scoped through PluginContext — the pipeline stamps the
// owning plugin id around every handler construction, so enforcement is structural: a handler
// can only ever write ITS OWN declared fields and service-read ITS OWN declared views.

/// <summary>The plugin on whose behalf the current handler (gate / effect handler / parked
/// work) was constructed. Scoped; stamped by the executor and the outbox dispatcher.</summary>
public sealed class PluginContext
{
    public string? PluginId { get; set; }
}

/// <summary>
/// The write half of packaged extension fields (docs/31 D-X2): a plugin sets a field it
/// DECLARED (docs/22 P2) on a host entity addressed by (entity wire key, row id) — no host CLR
/// type in the plugin's hands. Values pass the same semantic validation as the wire channel;
/// the write is audited with the PLUGIN as the attributed actor and live-refreshes open grids.
/// Compiled host fields and other plugins' keys are structurally unreachable.
/// </summary>
public interface IPackagedFieldWriter
{
    /// <summary>Sets this plugin's <paramref name="field"/> (unprefixed — the plugin prefix is
    /// applied structurally) on the row. False when the row no longer exists — subscribers stay
    /// idempotent over deletes instead of dead-lettering.</summary>
    Task<bool> SetAsync(string entityKey, Guid rowId, string field, object? value, CancellationToken ct);
}

public sealed class PackagedFieldWriter(
    ITamDb tam, TamModel model, PluginContext plugin, TenantScope scope,
    IServiceProvider services) : IPackagedFieldWriter
{
    public async Task<bool> SetAsync(
        string entityKey, Guid rowId, string field, object? value, CancellationToken ct)
    {
        var pluginId = plugin.PluginId
            ?? throw new InvalidOperationException(
                "PLG010: the packaged-field writer is only available to plugin handlers.");
        var fullKey = $"{pluginId}.{field}";
        var declared = model.PackagedFields.FirstOrDefault(p =>
                p.PluginId == pluginId && p.EntityKey == entityKey && p.Spec.Key == fullKey)
            ?? throw new InvalidOperationException(
                $"PLG010: plugin '{pluginId}' has not declared field '{fullKey}' on '{entityKey}'.");

        // Same validation the wire channel applies (docs/15): semantic rules + options.
        var semantic = declared.Spec.Semantic;
        var normalized = value is null ? null : semantic.Normalize(value);
        if (normalized is not null)
        {
            if (semantic.Validate(normalized) is not null)
                throw new InvalidOperationException(
                    $"PLG010: value for '{fullKey}' fails its declared semantic type.");
            if (declared.Spec.Options is { Count: > 0 } options
                && normalized is string s && !options.Contains(s))
                throw new InvalidOperationException(
                    $"PLG010: value for '{fullKey}' is not one of its declared options.");
        }

        // Entity + row by WIRE KEY: resolved through EF metadata, never a host CLR reference
        // in plugin code. Wrapped keys unwrap through the same ValueWrapper the model uses.
        var entityType = tam.Db.Model.GetEntityTypes()
            .FirstOrDefault(t => typeof(IExtensible).IsAssignableFrom(t.ClrType)
                && TamModel.EntityKey(t.ClrType) == entityKey)
            ?? throw new InvalidOperationException(
                $"PLG010: no extensible entity with wire key '{entityKey}'.");
        var keyType = entityType.FindPrimaryKey()!.Properties.Single().ClrType;
        var keyValue = keyType == typeof(Guid) ? rowId : ValueWrapper.Wrap(keyType, rowId);
        if (await tam.Db.FindAsync(entityType.ClrType, [keyValue], ct) is not IExtensible row)
            return false;   // row gone — idempotent no-op, not a poison message

        // FindAsync fetches by primary key ALONE — EF global query filters do not apply — so the
        // tenant boundary is re-checked here explicitly (review-round-4 F1). Without this, a
        // handler pinned to tenant A could write tenant B's row given its id; the RLS backstop
        // would catch it on Postgres, but the application must never rely on RLS for
        // correctness (docs/33 D-R1). Same idempotent no-op shape as a missing row.
        if (row is ITenantScoped scoped && scoped.TenantId != scope.Current)
            return false;

        row.Extensions = row.Extensions.WithValue(fullKey, normalized);

        // Plugin-attributed audit (docs/22 "deterministic composition"): provenance on the
        // write side, in the same transaction scope as the change itself.
        var audit = TamAudit.Capture(tam.Db, PluginActor(pluginId), $"plugin:{pluginId}.field-set");
        await tam.Db.SaveChangesAsync(ct);

        // Open grids refresh like any other committed write.
        (services.GetService(typeof(IEffectBackplane)) as IEffectBackplane)?.Send(
            audit.TenantId, audit.OperationId,
            TamAudit.InferEffects(audit).Select(e => (object)e).ToList());
        return true;
    }

    private OperationContext PluginActor(string pluginId) => new()
    {
        Actor = new Actor($"plugin:{pluginId}", pluginId, new HashSet<string>()),
        TenantId = new TenantId(scope.Current
            ?? throw new InvalidOperationException("PLG010: no ambient tenant for a plugin write.")),
        Source = InvocationSource.Internal,
        Culture = model.DefaultCulture,
        Services = services,
    };
}

/// <summary>
/// Reads host data BY VIEW ID through the real pipeline (docs/31 D-X3) — the blessing of the
/// fortnox pattern. Actor mode (a request exists): permission-checked and masked exactly like
/// the wire. Service mode (effect handlers — no actor): permitted ONLY for views this plugin
/// declared with <c>RequiresView</c>; the synthetic actor holds exactly that view's read atoms,
/// so the readable surface is the declared list the install screen shows — never a superuser.
/// </summary>
public interface IHostViewReader
{
    /// <summary>Actor mode: executes as the request's actor (gates, operations, mappers).</summary>
    Task<ViewResponse> RowsAsync(string viewId, IReadOnlyDictionary<string, string?> query,
        OperationContext context, CancellationToken ct);

    /// <summary>Service mode: declared views only (PLG011 otherwise), tenant-ambient.</summary>
    Task<ViewResponse> RowsAsync(string viewId, IReadOnlyDictionary<string, string?> query,
        CancellationToken ct);
}

public sealed class HostViewReader(
    TamModel model, ViewExecutor executor, PluginContext plugin, TenantScope scope,
    IServiceProvider services) : IHostViewReader
{
    public Task<ViewResponse> RowsAsync(string viewId, IReadOnlyDictionary<string, string?> query,
        OperationContext context, CancellationToken ct) => ExecuteAsync(viewId, query, context, ct);

    public Task<ViewResponse> RowsAsync(string viewId, IReadOnlyDictionary<string, string?> query,
        CancellationToken ct)
    {
        var pluginId = plugin.PluginId
            ?? throw new InvalidOperationException(
                "PLG011: the host view reader is only available to plugin handlers.");
        if (!model.ViewRequirements.Any(r => r.PluginId == pluginId && r.ViewId == viewId))
            throw new InvalidOperationException(
                $"PLG011: plugin '{pluginId}' did not declare RequiresView('{viewId}') — service-mode reads are whitelisted by declaration.");

        // Exactly the declared view's atoms: its permission plus its own widening atoms (an
        // ownership-scoped view read by a service is a roll-up over the whole node, docs/28).
        var view = model.Views[viewId];
        var grants = new HashSet<string> { view.Permission };
        foreach (var widens in view.DeclaringType
                     .GetCustomAttributes(typeof(WidensAttribute), inherit: false)
                     .Cast<WidensAttribute>())
            grants.Add(widens.Permission);

        var context = new OperationContext
        {
            Actor = new Actor($"plugin:{pluginId}", pluginId, grants),
            TenantId = new TenantId(scope.Current
                ?? throw new InvalidOperationException("PLG011: no ambient tenant for a service read.")),
            Source = InvocationSource.Internal,
            Culture = model.DefaultCulture,
            Services = services,
        };
        return ExecuteAsync(viewId, query, context, ct);
    }

    private async Task<ViewResponse> ExecuteAsync(string viewId,
        IReadOnlyDictionary<string, string?> query, OperationContext context, CancellationToken ct)
    {
        var (response, error) = await executor.ExecuteAsync(viewId, query, context, ct);
        if (error is not null)
            throw new InvalidOperationException($"host view '{viewId}': {error.Code}");
        return response!;
    }
}
