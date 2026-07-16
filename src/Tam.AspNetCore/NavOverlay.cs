using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

// Pipeline INFRASTRUCTURE, not package content (docs/29 litmus: this transform exists even if
// the web.nav admin surface doesn't — the manifest route applies it for every tenant). The
// tam.nav package in Packages/NavOverrides.cs owns the operations/view/grid that EDIT the rows.

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
