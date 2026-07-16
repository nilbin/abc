using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore.SystemOps;

/// <summary>
/// The tenant nav override registry (docs/30 v2, D-N5): the extension-field pattern applied to
/// navigation. Overrides are registry DATA — audited operations, retire-restores-the-default —
/// overlaid per tenant onto the declared tree at manifest build. Nav stays discoverability,
/// never authorization (D-N6): hiding removes the menu entry, not the surface.
/// </summary>
[TamPackage("tam.nav", "nav", "web.nav")]
public sealed class TamNavPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Nav(nav => nav.Page("nav", grid: "web.nav", suggest: "administration", order: 90));
        plugin.Model
            .AddOperationType(typeof(NavOverride))
            .AddOperationType(typeof(NavRetire))
            .AddViewType(typeof(NavOverrideList))
            // Enumerated only for the culture-text renderer on Labels (docs/32 D-P6 deviation).
            .Form<NavOverride.Input>("web.nav.override", "nav.override", form =>
            {
                form.Field(x => x.NodeId);
                form.Field(x => x.Hidden);
                form.Field(x => x.Labels).Renderer("culture-text");
                form.Field(x => x.Order);
                form.Field(x => x.Parent);
            })
            .Grid<NavOverrideList.Result>("web.nav", "nav.overrides", grid =>
            {
                grid.ToolbarAction("nav.override");
                grid.RowAction("nav.retire");
            });
    }
}

public static class NavFindings
{
    public static readonly FindingFactory UnknownNode = Finding.Error("nav.unknown-node");
    public static readonly FindingFactory InvalidParent = Finding.Error("nav.invalid-parent");
}

/// <summary>
/// Applies a tenant's overrides to the manifest nav trees: hidden removes the node (subtree
/// included), order replaces, parent moves a PAGE under a section, labels merge into the
/// per-culture catalogs under the node's "nav.{id}" key. An override whose node — or move
/// target — is absent from the tree (plugin deactivated, host restructure) is DORMANT, not
/// broken: nothing renders wrong, and it comes back intact when the node does (docs/30 v2).
/// </summary>
public static class NavOverlay
{
    public static ManifestDto Apply(ManifestDto manifest, IReadOnlyList<NavOverrideEntity> overrides)
    {
        if (overrides.Count == 0) return manifest;
        var byNode = overrides.ToDictionary(o => o.NodeId, StringComparer.Ordinal);
        return manifest with
        {
            Nav = manifest.Nav.ToDictionary(
                kv => kv.Key,
                kv => ApplySurface(kv.Value, byNode),
                StringComparer.Ordinal),
            Catalogs = MergeLabels(manifest.Catalogs, overrides),
        };
    }

    /// <summary>Content-derived fingerprint for the manifest revision/ETag: any override change
    /// must move it, or clients keep rendering the pre-override tree from cache.</summary>
    public static string Fingerprint(IReadOnlyList<NavOverrideEntity> overrides) =>
        string.Join("|", overrides
            .OrderBy(o => o.NodeId, StringComparer.Ordinal)
            .Select(o => $"{o.NodeId}/{o.Hidden}/{o.Order}/{o.Parent}/{o.LabelsJson}"));

    private static IReadOnlyList<ManifestNavNode> ApplySurface(
        IReadOnlyList<ManifestNavNode> tree, Dictionary<string, NavOverrideEntity> byNode)
    {
        // Hide + reorder first, THEN resolve moves against the surviving tree: a move into a
        // hidden (or absent) section is dormant — the page stays where the host put it.
        var visible = PruneAndOrder(tree, byNode);
        var sections = new HashSet<string>(StringComparer.Ordinal);
        CollectSections(visible, sections);

        var moved = new Dictionary<string, List<ManifestNavNode>>(StringComparer.Ordinal);
        var detached = Detach(visible, byNode, sections, moved);
        return moved.Count == 0 ? detached : Attach(detached, moved);
    }

    private static IReadOnlyList<ManifestNavNode> PruneAndOrder(
        IReadOnlyList<ManifestNavNode> nodes, Dictionary<string, NavOverrideEntity> byNode)
    {
        var result = new List<ManifestNavNode>();
        foreach (var node in nodes)
        {
            byNode.TryGetValue(node.Id, out var over);
            if (over is { Hidden: true }) continue;
            result.Add(node with
            {
                Order = over?.Order ?? node.Order,
                Children = PruneAndOrder(node.Children, byNode),
            });
        }
        return result;
    }

    private static void CollectSections(IReadOnlyList<ManifestNavNode> nodes, HashSet<string> sections)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == "section") sections.Add(node.Id);
            CollectSections(node.Children, sections);
        }
    }

    private static IReadOnlyList<ManifestNavNode> Detach(
        IReadOnlyList<ManifestNavNode> nodes, Dictionary<string, NavOverrideEntity> byNode,
        HashSet<string> sections, Dictionary<string, List<ManifestNavNode>> moved)
    {
        var result = new List<ManifestNavNode>();
        foreach (var node in nodes)
        {
            var kept = node with { Children = Detach(node.Children, byNode, sections, moved) };
            if (node.Kind == "page"
                && byNode.TryGetValue(node.Id, out var over)
                && over.Parent is { Length: > 0 } target
                && sections.Contains(target))
            {
                (moved.TryGetValue(target, out var list)
                    ? list
                    : moved[target] = []).Add(kept);
                continue;
            }
            result.Add(kept);
        }
        return result;
    }

    private static IReadOnlyList<ManifestNavNode> Attach(
        IReadOnlyList<ManifestNavNode> nodes, Dictionary<string, List<ManifestNavNode>> moved) =>
        nodes.Select(node => node with
        {
            Children = node.Kind == "section" && moved.TryGetValue(node.Id, out var arrivals)
                ? [.. Attach(node.Children, moved), .. arrivals]
                : Attach(node.Children, moved),
        }).ToList();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MergeLabels(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> catalogs,
        IReadOnlyList<NavOverrideEntity> overrides)
    {
        var merged = catalogs.ToDictionary(
            kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        foreach (var over in overrides)
            foreach (var (culture, text) in over.Labels())
                if (text.Length > 0 && merged.TryGetValue(culture, out var catalog))
                    catalog[$"nav.{over.NodeId}"] = text;
        return merged.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value);
    }
}

/// <summary>
/// Upserts the tenant's override for one node — the CLOSED mutation set (D-N6: closed targets
/// keep overrides validatable): hidden, per-culture labels, order, parent. Validated against
/// the COMPILED tree (activation is runtime data — overriding an inactive plugin's node is
/// legal and dormant). One row per node; the row IS the override state.
/// </summary>
[Operation("nav.override")]
[Authorize("nav.manage")]
public static class NavOverride
{
    public sealed record Input(
        string NodeId,
        bool Hidden = false,
        Dictionary<string, string>? Labels = null,
        int? Order = null,
        string? Parent = null);

    public sealed record Output(Guid OverrideId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        var node = Find(model, input.NodeId);
        if (node is null)
            return NavFindings.UnknownNode.With(("node", input.NodeId)).At(nameof(Input.NodeId));

        if (input.Parent is { Length: > 0 } parent)
        {
            // Only pages move, and only under sections — modes and sections are the host's frame.
            var target = Find(model, parent);
            if (node.Kind != NavNodeKind.Page
                || target is null || target.Kind != NavNodeKind.Section)
                return NavFindings.InvalidParent.With(("parent", parent)).At(nameof(Input.Parent));
        }

        var row = await tam.Db.Set<NavOverrideEntity>()
            .SingleOrDefaultAsync(x => x.NodeId == input.NodeId, ct);
        if (row is null)
        {
            row = new NavOverrideEntity { Id = Guid.NewGuid(), NodeId = input.NodeId };
            tam.Db.Add(row);
        }
        row.Hidden = input.Hidden;
        row.Order = input.Order;
        row.Parent = input.Parent;
        row.LabelsJson = JsonSerializer.Serialize(input.Labels ?? []);
        return new Output(row.Id);
    }

    internal static NavNode? Find(TamModel model, string id)
    {
        foreach (var tree in model.Nav.Values)
            if (FindIn(tree, id) is { } node) return node;
        return null;

        static NavNode? FindIn(IReadOnlyList<NavNode> nodes, string id)
        {
            foreach (var node in nodes)
                if (node.Id == id) return node;
                else if (FindIn(node.Children, id) is { } hit) return hit;
            return null;
        }
    }
}

/// <summary>Restores the declared default: the override row is DELETED — unlike extension
/// fields there is no data behind it and the node id stays addressable, so retire-don't-delete
/// has nothing to preserve here.</summary>
[Operation("nav.retire")]
[Authorize("nav.manage")]
public static class NavRetire
{
    public sealed record Input(string NodeId);

    public sealed record Output(string NodeId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var row = await tam.Db.Set<NavOverrideEntity>()
            .SingleOrDefaultAsync(x => x.NodeId == input.NodeId, ct);
        if (row is null) return PipelineFindings.NotFound.Create();

        tam.Db.Remove(row);
        return new Output(row.NodeId);
    }
}

[View("nav.overrides")]
[Authorize("nav.manage")]
public static class NavOverrideList
{
    public sealed record Query;

    public sealed record Result
    {
        public Guid Id { get; init; }
        public string NodeId { get; init; } = "";
        public bool Hidden { get; init; }
        public int? Order { get; init; }
        public string? Parent { get; init; }
        public string Labels { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context) =>
        tam.Db.Set<NavOverrideEntity>().Select(x => new Result
        {
            Id = x.Id, NodeId = x.NodeId, Hidden = x.Hidden,
            Order = x.Order, Parent = x.Parent, Labels = x.LabelsJson,
        });

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.NodeId)).DefaultSort(nameof(Result.NodeId));
}
