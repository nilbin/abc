using System.Reflection;

namespace Tam;

// Build()-time verification (docs/29): the NAV/PLG/L10N gates and the nav merge — the
// parts of TamModelBuilder that CHECK a model rather than author one.
public sealed partial class TamModelBuilder
{
    /// <summary>
    /// The docs/30 merge, per surface: the host tree verbatim; placement markers resolved to
    /// contributions; sections whose id matches contributions' SUGGEST slugs collect them after
    /// explicit children; on the "web" surface, everything left over — uncollected contributions
    /// and plugins with grids no node references — lands under the well-known "more" section in
    /// the LAST mode, so nothing can be authored into invisibility (D-N1).
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<NavNode>> MergeNav(
        IReadOnlyDictionary<string, GridDefinition> gridDefs)
    {
        var result = new Dictionary<string, IReadOnlyList<NavNode>>();
        foreach (var (surface, tree) in navTrees)
        {
            var consumed = new HashSet<NavContribution>();
            var byId = navContributions.ToDictionary(c => c.Page.Id);

            NavNode Resolve(NavNode node)
            {
                if (ReferenceEquals(node.Target, NavNodeBuilder.PlacementMarker))
                {
                    if (!byId.TryGetValue(node.Id, out var placed))
                        throw new InvalidOperationException(
                            $"NAV003: Place('{node.Id}') matches no contributed nav node.");
                    consumed.Add(placed);
                    // Placement is the HOST's — including position: the marker's order (host-
                    // supplied, default none) replaces the contribution's suggestion-tier order.
                    return placed.Page with { Order = node.Order };
                }
                var children = node.Children.Select(Resolve).ToList();
                if (node.Kind == NavNodeKind.Section)
                {
                    foreach (var c in navContributions.Where(c => c.Suggest == node.Id && consumed.Add(c)))
                        children.Add(c.Page);
                }
                // Stable order-then-declaration sort: contributions from many registrants
                // interleave predictably; undeclared orders append.
                children = children
                    .Select((c, i) => (c, i))
                    .OrderBy(x => x.c.Order ?? int.MaxValue).ThenBy(x => x.i)
                    .Select(x => x.c).ToList();
                return node with { Children = children };
            }

            var modes = tree.Modes.Select(Resolve).ToList();

            if (surface == "web" && modes.Count > 0)
            {
                var referenced = new HashSet<string>();
                void Walk(NavNode n)
                {
                    if (n.Target?.Grid is { } g) referenced.Add(g);
                    foreach (var c in n.Children) Walk(c);
                }
                foreach (var m in modes) Walk(m);

                var leftovers = new List<NavNode>();
                leftovers.AddRange(navContributions.Where(c => !consumed.Contains(c)).Select(c => c.Page));
                // Plugins whose grids no node references get the generic per-plugin page —
                // exactly the pre-nav behavior, now as the declared model's safety net. A plugin
                // that declared ANY nav contribution has graduated (docs/30 D-N1): its
                // declaration is authoritative and it never also gets the mechanical page.
                var declared = navContributions.Select(c => c.Plugin).ToHashSet();
                foreach (var plugin in gridDefs.Values
                             .Where(g => g.Plugin is not null && !referenced.Contains(g.Id))
                             .Select(g => g.Plugin!).Distinct().Order())
                {
                    if (declared.Contains(plugin)) continue;
                    leftovers.Add(new NavNode(plugin, NavNodeKind.Page, $"plugins.{plugin}.title",
                        null, null, new NavTarget(Plugin: plugin), null, plugin, []));
                }

                if (leftovers.Count > 0)
                {
                    var last = modes[^1];
                    var more = new NavNode(NavNode.More, NavNodeKind.Section, $"nav.{NavNode.More}",
                        null, null, null, null, null, leftovers);
                    modes[^1] = last with { Children = [.. last.Children, more] };
                }
            }
            result[surface] = modes;
        }
        return result;
    }

    /// <summary>NAV001 duplicate ids, NAV002 depth cap (mode + 3), NAV004 unknown grid target,
    /// NAV005 page targets need an EXISTING catalogue atom. Contribution ids are namespace-
    /// checked with everything else in <see cref="VerifyPluginNamespaces"/>.</summary>
    private static void VerifyNav(TamModel model)
    {
        var permissions = model.Permissions.ToHashSet();
        foreach (var (surface, modes) in model.Nav)
        {
            var seen = new HashSet<string>();
            void Walk(NavNode node, int depth)
            {
                if (!seen.Add(node.Id))
                    throw new InvalidOperationException(
                        $"NAV001: duplicate nav node id '{node.Id}' on surface '{surface}'.");
                if (depth > 3)
                    throw new InvalidOperationException(
                        $"NAV002: nav node '{node.Id}' exceeds the depth cap (mode + 3) on surface '{surface}'.");
                if (node.Target?.Grid is { } grid && !model.Grids.ContainsKey(grid))
                    throw new InvalidOperationException(
                        $"NAV004: nav node '{node.Id}' targets unknown grid '{grid}'.");
                if (node.Target?.Page is { } && node.Permission is null)
                    throw new InvalidOperationException(
                        $"NAV005: nav node '{node.Id}' has a page target and needs an explicit permission.");
                if (node.Permission is { } atom && !permissions.Contains(atom))
                    throw new InvalidOperationException(
                        $"NAV005: nav node '{node.Id}' permission '{atom}' is not in the compiled catalogue.");
                foreach (var child in node.Children) Walk(child, depth + 1);
            }
            foreach (var mode in modes) Walk(mode, 0);
        }
    }

    /// <summary>
    /// PLG001: everything a plugin contributes lives under its permanent id prefix — operation,
    /// view, form and grid ids, and the permissions they declare. Collisions between plugins and
    /// host (or each other) are thereby impossible by construction.
    /// </summary>
    /// <summary>The widening atoms (docs/28) a type declares — checked against the plugin
    /// namespace like any other permission, so a plugin can't mint a HOST widening atom
    /// (e.g. [Widens("orders.read-all")]) into the compiled catalogue.</summary>
    private static IEnumerable<string> Widens(Type declaringType) =>
        declaringType.GetCustomAttributes(typeof(WidensAttribute), inherit: false)
            .Cast<WidensAttribute>()
            .Select(w => w.Permission);

    private static void VerifyPluginNamespaces(TamModel model)
    {
        var violations = new List<string>();

        void Check(string kind, string id, string? plugin, string? permission = null)
        {
            if (plugin is null) return;
            // A PACKAGE (framework tier) owns its CLAIMED prefixes; a plugin owns "{id}.".
            if (model.Packages.TryGetValue(plugin, out var package))
            {
                bool Claimed(string value) =>
                    package.Prefixes.Any(p => value == p
                        || value.StartsWith(p + ".", StringComparison.Ordinal));
                if (!Claimed(id))
                    violations.Add($"{kind} '{id}' is outside package '{plugin}' claimed prefixes");
                if (permission is not null && !Claimed(permission))
                    violations.Add($"{kind} '{id}' permission '{permission}' is outside package '{plugin}' claimed prefixes");
                return;
            }
            var prefix = plugin + ".";
            if (!id.StartsWith(prefix, StringComparison.Ordinal))
                violations.Add($"{kind} '{id}' is not under '{prefix}'");
            if (permission is not null && !permission.StartsWith(prefix, StringComparison.Ordinal))
                violations.Add($"{kind} '{id}' permission '{permission}' is not under '{prefix}'");
        }

        foreach (var o in model.Operations.Values)
        {
            Check("operation", o.Id, o.Plugin, o.Permission);
            foreach (var w in Widens(o.DeclaringType)) Check("operation", o.Id, o.Plugin, w);
        }
        foreach (var v in model.Views.Values)
        {
            Check("view", v.Id, v.Plugin, v.Permission);
            foreach (var w in Widens(v.DeclaringType)) Check("view", v.Id, v.Plugin, w);
        }
        foreach (var f in model.Forms.Values) Check("form", f.Id, f.Plugin);
        foreach (var g in model.Grids.Values) Check("grid", g.Id, g.Plugin);
        foreach (var nodes in model.Nav.Values)
        {
            void Walk(NavNode n)
            {
                if (n.Plugin is not null && n.Target?.Plugin is null)
                    Check("nav node", n.Id, n.Plugin);
                foreach (var c in n.Children) Walk(c);
            }
            foreach (var n in nodes) Walk(n);
        }

        if (violations.Count > 0)
            throw new InvalidOperationException(
                $"PLG001: plugin contributions outside their namespace: {string.Join("; ", violations)}.");
    }

    /// <summary>L10N001: every label key the model references must exist in the default culture.</summary>
    private static void VerifyLocalization(TamModel model, LocaleCatalogs catalogs)
    {
        static IEnumerable<string> NavLabels(NavNode node) =>
            new[] { node.LabelKey }.Concat(node.Children.SelectMany(NavLabels));
        var required = model.Operations.Values.SelectMany(o => o.InputFields.Select(f => f.LabelKey))
            .Concat(model.Operations.Values.Select(o => o.TitleKey))
            .Concat(model.Views.Values.SelectMany(v => v.ResultFields.Select(f => f.LabelKey)))
            .Concat(model.Plugins.Values.Select(p => p.TitleKey))
            .Concat(model.Nav.Values.SelectMany(nodes => nodes.SelectMany(NavLabels)));

        var missing = catalogs.MissingKeys(required, catalogs.DefaultCulture);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"L10N001: {missing.Count} key(s) missing in default culture '{catalogs.DefaultCulture}': " +
                string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", …" : string.Empty));
        }
    }

    private sealed class LocaleCatalogsBuilder
    {
        public List<string> Directories { get; } = [];

        public List<(string Culture, IReadOnlyDictionary<string, string> Entries)> Defaults { get; } = [];
    }
}
