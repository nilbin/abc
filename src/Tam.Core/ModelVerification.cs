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
    /// <summary>PLG006: grid action contributions — target grid exists, the operation is the
    /// CONTRIBUTING plugin's own, every bound input exists on the operation and every bound
    /// column on the target grid's view Result. PLG008: declared view requirements name a real
    /// view and real result fields. Duplicates rejected like PLG002.</summary>
    private static void VerifyContributions(TamModel model)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var action in model.GridActions.Values.SelectMany(a => a))
        {
            if (!seen.Add((action.GridId, action.OperationId)))
                throw new InvalidOperationException(
                    $"PLG006: plugin '{action.PluginId}' contributes '{action.OperationId}' to grid '{action.GridId}' more than once.");
            if (!model.Grids.TryGetValue(action.GridId, out var grid))
                throw new InvalidOperationException(
                    $"PLG006: grid action targets unknown grid '{action.GridId}'.");
            if (!model.Operations.TryGetValue(action.OperationId, out var operation))
                throw new InvalidOperationException(
                    $"PLG006: grid action names unknown operation '{action.OperationId}'.");
            if (operation.Plugin != action.PluginId)
                throw new InvalidOperationException(
                    $"PLG006: plugin '{action.PluginId}' may only contribute ITS OWN operations as grid actions — '{action.OperationId}' is not.");
            var view = model.Views[grid.ViewId];
            foreach (var (input, column) in action.Bind)
            {
                if (!operation.InputFields.Any(fld => fld.WireName == input))
                    throw new InvalidOperationException(
                        $"PLG006: grid action '{action.OperationId}' binds unknown input '{input}'.");
                if (column != "id" && !view.ResultFields.Any(fld => fld.WireName == column))
                    throw new InvalidOperationException(
                        $"PLG006: grid action '{action.OperationId}' binds column '{column}', not on view '{grid.ViewId}'.");
            }
        }

        foreach (var requirement in model.ViewRequirements)
        {
            if (!model.Views.TryGetValue(requirement.ViewId, out var view))
                throw new InvalidOperationException(
                    $"PLG008: plugin '{requirement.PluginId}' requires unknown view '{requirement.ViewId}'.");
            foreach (var field in requirement.Fields)
                if (field != "id" && !view.ResultFields.Any(fld => fld.WireName == field))
                    throw new InvalidOperationException(
                        $"PLG008: plugin '{requirement.PluginId}' requires field '{field}' which view '{requirement.ViewId}' does not expose.");
        }
    }

    /// <summary>PLG007: panels land in a declared slot, use the plugin's OWN grid, and bind
    /// real query fields to real context keys. PLG009: every OnEffect / RequiresEvent target
    /// names a DECLARED event carrying the required payload fields — subscriptions are
    /// contracts, not folklore. Plugin-declared events must sit under the plugin prefix.</summary>
    private static void VerifySlotsAndEvents(
        TamModel model, IReadOnlyList<EventRequirement> eventRequirements)
    {
        foreach (var panel in model.Panels.Values.SelectMany(p => p))
        {
            if (!model.Slots.TryGetValue(panel.SlotId, out var slot))
                throw new InvalidOperationException(
                    $"PLG007: panel targets unknown slot '{panel.SlotId}'.");
            if (!model.Grids.TryGetValue(panel.GridId, out var grid))
                throw new InvalidOperationException(
                    $"PLG007: panel names unknown grid '{panel.GridId}'.");
            if (grid.Plugin != panel.PluginId)
                throw new InvalidOperationException(
                    $"PLG007: plugin '{panel.PluginId}' may only contribute ITS OWN grids as panels — '{panel.GridId}' is not.");
            var view = model.Views[grid.ViewId];
            foreach (var (queryField, contextKey) in panel.Bind)
            {
                if (!view.QueryFields.Any(f => f.WireName == queryField))
                    throw new InvalidOperationException(
                        $"PLG007: panel '{panel.GridId}' binds unknown query field '{queryField}'.");
                if (!slot.ContextKeys.Contains(contextKey))
                    throw new InvalidOperationException(
                        $"PLG007: panel '{panel.GridId}' binds context key '{contextKey}', not provided by slot '{panel.SlotId}'.");
            }
        }

        foreach (var declared in model.Events.Values)
            if (declared.Plugin is { } owner
                && !declared.EventType.StartsWith(owner + ".", StringComparison.Ordinal)
                && !model.Packages.ContainsKey(owner))
                throw new InvalidOperationException(
                    $"PLG001: event '{declared.EventType}' is not under '{owner}.'.");

        foreach (var subscriber in model.Subscribers)
            if (!model.Events.ContainsKey(subscriber.EventType))
                throw new InvalidOperationException(
                    $"PLG009: plugin '{subscriber.PluginId}' subscribes to undeclared event '{subscriber.EventType}' — declare it with PublishesEvent.");
        foreach (var outbound in model.OutboundIntegrations.Values)
            if (outbound.Trigger is EventTrigger trigger && !model.Events.ContainsKey(trigger.EventType))
                throw new InvalidOperationException(
                    $"PLG009: outbound integration '{outbound.Id}' triggers on undeclared event '{trigger.EventType}' — declare it with PublishesEvent.");

        foreach (var requirement in eventRequirements)
        {
            if (!model.Events.TryGetValue(requirement.EventType, out var declared)
                )
                throw new InvalidOperationException(
                    $"PLG009: plugin '{requirement.PluginId}' requires undeclared event '{requirement.EventType}'.");
            foreach (var field in requirement.Fields)
                if (!declared.Fields.Contains(field))
                    throw new InvalidOperationException(
                        $"PLG009: event '{requirement.EventType}' does not carry field '{field}' required by plugin '{requirement.PluginId}'.");
        }
    }

    /// <summary>PAGE001: a declared page's parts must all exist and fit — grids, page-level
    /// slots, the record's detail view (and the context key on its Query), forms, the title
    /// field, record slots. SLOT001: a declared slot referenced by NO page and not marked
    /// external is authored into invisibility — plugins would contribute panels nothing
    /// renders (the nav "more" lesson, applied to slots).</summary>
    private static void VerifyPages(TamModel model)
    {
        foreach (var page in model.Pages.Values)
        {
            foreach (var section in page.Sections)
            {
                if (section.Kind == PageSection.GridKind && !model.Grids.ContainsKey(section.Id))
                    throw new InvalidOperationException(
                        $"PAGE001: page '{page.Id}' names unknown grid '{section.Id}'.");
                if (section.Kind == PageSection.SlotKind && !model.Slots.ContainsKey(section.Id))
                    throw new InvalidOperationException(
                        $"PAGE001: page '{page.Id}' references undeclared slot '{section.Id}'.");
            }
            if (page.Record is not { } record) continue;

            if (!model.Views.TryGetValue(record.DetailViewId, out var detail))
                throw new InvalidOperationException(
                    $"PAGE001: page '{page.Id}' names unknown detail view '{record.DetailViewId}'.");
            if (!detail.QueryFields.Any(f => f.WireName == record.ContextKey))
                throw new InvalidOperationException(
                    $"PAGE001: page '{page.Id}' key '{record.ContextKey}' is not a query field of '{record.DetailViewId}'.");
            if (record.TitleField is { } title && !detail.ResultFields.Any(f => f.WireName == title))
                throw new InvalidOperationException(
                    $"PAGE001: page '{page.Id}' title field '{title}' is not on '{record.DetailViewId}'.");
            foreach (var section in record.Sections)
            {
                if (section.Kind == RecordSection.FormKind && !model.Forms.ContainsKey(section.Id))
                    throw new InvalidOperationException(
                        $"PAGE001: page '{page.Id}' names unknown form '{section.Id}'.");
                if (section.Kind == RecordSection.SlotKind && !model.Slots.ContainsKey(section.Id))
                    throw new InvalidOperationException(
                        $"PAGE001: page '{page.Id}' references undeclared slot '{section.Id}'.");
            }
        }

        var referenced = model.Pages.Values.SelectMany(p =>
                p.Sections.Where(s => s.Kind == PageSection.SlotKind).Select(s => s.Id)
                    .Concat(p.Record?.Sections
                        .Where(s => s.Kind == RecordSection.SlotKind).Select(s => s.Id) ?? []))
            .ToHashSet();
        foreach (var slot in model.Slots.Values)
            if (!slot.External && !referenced.Contains(slot.Id))
                throw new InvalidOperationException(
                    $"SLOT001: slot '{slot.Id}' is referenced by no declared page — panels contributed to it would never render. Reference it from a page, or declare it external: true (placed by app code).");
    }

    /// <summary>SUB001: a subtree-read view's tenant field must be a real result field —
    /// the client renders the company column, tenant filter and row-action targeting off it.</summary>
    private static void VerifySubtreeViews(TamModel model)
    {
        foreach (var view in model.Views.Values)
        {
            if (view.Capabilities.SubtreeTenantField is not { } field) continue;
            if (!view.ResultFields.Any(f => f.WireName == field))
                throw new InvalidOperationException(
                    $"SUB001: view '{view.Id}' declares SubtreeRead over '{field}', which is not a result field.");
        }
    }

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
                if (node.Target?.Page is { } pageKey && node.Permission is null
                    && !model.Pages.ContainsKey(pageKey))
                    throw new InvalidOperationException(
                        $"NAV005: nav node '{node.Id}' has a page target and needs an explicit permission (declared pages derive it from their grid).");
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
