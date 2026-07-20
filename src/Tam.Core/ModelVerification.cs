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
                // A suggestion slug collects into a matching SECTION or MODE (review round 4:
                // both docs-only implementers suggested "work" — the mode — and silently landed
                // under "more"; the semantic slug should match whatever grouping the host named
                // that way, D-N2 spirit). Sections resolve first only by tree order: Resolve
                // recurses depth-first, so a section named like its mode still wins its own pass.
                if (node.Kind is NavNodeKind.Section or NavNodeKind.Mode)
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
                // that DECLARED nav — even `nav.None()` — has graduated (docs/30 D-N1): its
                // declaration is authoritative and it never also gets the mechanical page.
                var declared = navDeclaringPlugins;
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
                    var more = new NavNode(NavNode.More, NavNodeKind.Section, LabelKeys.Nav(NavNode.More),
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
            VerifyContractOwnership(model, requirement.PluginId, view.Plugin,
                $"view '{requirement.ViewId}'");
            foreach (var field in requirement.Fields)
                if (field != "id" && !view.ResultFields.Any(fld => fld.WireName == field))
                    throw new InvalidOperationException(
                        $"PLG008: plugin '{requirement.PluginId}' requires field '{field}' which view '{requirement.ViewId}' does not expose.");
            // Declared kinds against the LIVE wire kinds (the PLG009 discipline for reads):
            // a facade compiled from yesterday's artifact fails the build when the view's
            // type drifted, instead of misreading rows at runtime. Undeclared (string-ish)
            // actual kinds are never checked — string stays the open end of the grammar.
            foreach (var (field, declared) in requirement.Kinds)
            {
                var resultField = view.ResultFields.FirstOrDefault(fld => fld.WireName == field);
                if (resultField is null) continue;   // absence already reported above
                var actual = ContractKinds.FromClr(resultField.EffectiveType);
                if (actual is not null && actual != declared)
                    throw new InvalidOperationException(
                        $"PLG008: plugin '{requirement.PluginId}' requires field '{field}' of view "
                        + $"'{requirement.ViewId}' as '{declared}' but the view exposes '{actual}'.");
            }
        }
    }

    /// <summary>PLG010: the docs/22 dependency rule, made mechanical — a PLUGIN may consume
    /// contracts owned by the HOST, by a framework PACKAGE (always-on, host-like trust), by
    /// ITSELF, or by another plugin it has DECLARED a dependency on (docs/37 D-V4 — a sanctioned,
    /// acyclic edge, verified by PLG011 before we get here). Absent such an edge the old rule
    /// stands: promote the shared concept into the host, or merge the plugins. Packages
    /// themselves are exempt as consumers.</summary>
    private static void VerifyContractOwnership(
        TamModel model, string consumerId, string? ownerId, string contract)
    {
        if (model.Packages.ContainsKey(consumerId)) return;
        if (ownerId is null || ownerId == consumerId || model.Packages.ContainsKey(ownerId))
            return;
        if (model.Plugins.ContainsKey(ownerId) && DependsOnTransitively(model, consumerId, ownerId))
            return;
        throw new InvalidOperationException(
            $"PLG010: plugin '{consumerId}' depends on {contract} owned by plugin '{ownerId}' — "
            + "plugins depend on the HOST's contract, not on each other, unless the consumer "
            + $"declares DependsOn(\"{ownerId}\") (docs/37). Promote the shared concept into the "
            + "host, merge the plugins, or declare the dependency edge.");
    }

    /// <summary>Does <paramref name="consumerId"/> reach <paramref name="ownerId"/> through the
    /// declared DependsOn graph? Chains are legal at any depth (docs/37 D-V4); the graph is
    /// acyclic (PLG011 ran first), and the visited set is belt-and-suspenders.</summary>
    private static bool DependsOnTransitively(TamModel model, string consumerId, string ownerId)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        if (model.Plugins.TryGetValue(consumerId, out var start))
            foreach (var d in start.DependsOn) stack.Push(d);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == ownerId) return true;
            if (!seen.Add(current)) continue;
            if (model.Plugins.TryGetValue(current, out var def))
                foreach (var d in def.DependsOn) stack.Push(d);
        }
        return false;
    }

    /// <summary>PLG011: the plugin relationship graph is well-formed (docs/37 D-V4) — every
    /// DependsOn edge targets a REGISTERED plugin (a package is always-on and needs no edge; the
    /// host is not a plugin), no plugin depends on itself, and there are NO CYCLES. Runs before
    /// the PLG010 sites so the acyclic-edge fact they trust is already proven.</summary>
    private static void VerifyPluginRelationships(TamModel model)
    {
        foreach (var (id, def) in model.Plugins)
            foreach (var dep in def.DependsOn)
            {
                if (dep == id)
                    throw new InvalidOperationException(
                        $"PLG011: plugin '{id}' declares DependsOn on itself.");
                if (!model.Plugins.ContainsKey(dep))
                    throw new InvalidOperationException(
                        $"PLG011: plugin '{id}' declares DependsOn('{dep}') but no such plugin is "
                        + (model.Packages.ContainsKey(dep)
                            ? "a plugin — '" + dep + "' is a framework package, always available without an edge."
                            : "registered."));
            }

        // Cycle detection: DFS with a three-colour marking, reporting the offending path.
        var colour = new Dictionary<string, int>();   // 0 unvisited, 1 in-progress, 2 done
        var path = new List<string>();
        void Visit(string id)
        {
            colour[id] = 1;
            path.Add(id);
            foreach (var dep in model.Plugins[id].DependsOn)
            {
                if (!model.Plugins.ContainsKey(dep)) continue;   // reported above
                if (colour.GetValueOrDefault(dep) == 1)
                    throw new InvalidOperationException(
                        $"PLG011: dependency cycle {string.Join(" → ", path.Skip(path.IndexOf(dep)).Append(dep))}.");
                if (colour.GetValueOrDefault(dep) == 0)
                    Visit(dep);
            }
            path.RemoveAt(path.Count - 1);
            colour[id] = 2;
        }
        foreach (var id in model.Plugins.Keys)
            if (colour.GetValueOrDefault(id) == 0)
                Visit(id);
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
        {
            if (declared.Plugin is { } owner
                && !declared.EventType.StartsWith(owner + ".", StringComparison.Ordinal)
                && !model.Packages.ContainsKey(owner))
                throw new InvalidOperationException(
                    $"PLG001: event '{declared.EventType}' is not under '{owner}.'.");
            // The `rules.` prefix is RESERVED for tenant automation-rule publish-event actions
            // (review round 5): a package declaring `rules.{x}` could later collide with a
            // tenant's already-defined rule and turn it into a forged-payload source.
            if (declared.EventType.StartsWith("rules.", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"PLG009: event '{declared.EventType}' uses the reserved 'rules.' prefix — "
                    + "that namespace belongs to tenant automation-rule actions.");
        }

        // "*" mirrors GateDefinition.Wildcard: a subscriber that runs on EVERY committed
        // event and decides from model data (the magic-folder bindings) whether to act.
        foreach (var subscriber in model.Subscribers)
        {
            if (subscriber.EventType == "*") continue;
            if (!model.Events.TryGetValue(subscriber.EventType, out var target))
                throw new InvalidOperationException(
                    $"PLG009: plugin '{subscriber.PluginId}' subscribes to undeclared event '{subscriber.EventType}' — declare it with PublishesEvent.");
            VerifyContractOwnership(model, subscriber.PluginId, target.Plugin,
                $"event '{subscriber.EventType}'");
        }

        // DOC001 (docs/35 magic folders): a binding must target a declared event, and every
        // "{placeholder}" must name a field of that event's payload contract.
        foreach (var binding in model.DocumentFolders)
        {
            if (!model.Events.TryGetValue(binding.EventType, out var declared))
                throw new InvalidOperationException(
                    $"DOC001: document folder binding targets undeclared event '{binding.EventType}' — declare it with PublishesEvent.");
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(binding.PathTemplate, "\\{([^}]*)\\}"))
                if (!declared.Fields.Contains(match.Groups[1].Value))
                    throw new InvalidOperationException(
                        $"DOC001: template '{binding.PathTemplate}' names '{{{match.Groups[1].Value}}}', not a payload field of '{binding.EventType}'.");
        }
        foreach (var outbound in model.OutboundIntegrations.Values)
            if (outbound.Trigger is EventTrigger trigger)
            {
                if (!model.Events.TryGetValue(trigger.EventType, out var triggerEvent))
                    throw new InvalidOperationException(
                        $"PLG009: outbound integration '{outbound.Id}' triggers on undeclared event '{trigger.EventType}' — declare it with PublishesEvent.");
                // Triggering on another plugin's event is consuming its contract — PLG010 applies
                // here too (closing the outbound-trigger gap), lifted only by a declared edge.
                VerifyContractOwnership(model, outbound.PluginId, triggerEvent.Plugin,
                    $"event '{trigger.EventType}'");
            }

        foreach (var requirement in eventRequirements)
        {
            if (!model.Events.TryGetValue(requirement.EventType, out var declared))
                throw new InvalidOperationException(
                    $"PLG009: plugin '{requirement.PluginId}' requires undeclared event '{requirement.EventType}'.");
            VerifyContractOwnership(model, requirement.PluginId, declared.Plugin,
                $"event '{requirement.EventType}'");
            foreach (var field in requirement.Fields)
            {
                if (!declared.Fields.Contains(field))
                    throw new InvalidOperationException(
                        $"PLG009: event '{requirement.EventType}' does not carry field '{field}' required by plugin '{requirement.PluginId}'.");
                // Where BOTH sides declare a kind they must agree — the publisher owns the
                // shape; a consumer's facade must not read a guid as a decimal.
                if (requirement.Kinds.TryGetValue(field, out var required)
                    && declared.Kinds.TryGetValue(field, out var published)
                    && required != published)
                    throw new InvalidOperationException(
                        $"PLG009: event '{requirement.EventType}' field '{field}' is published as "
                        + $"'{published}' but required as '{required}' by plugin '{requirement.PluginId}' — the kinds must agree.");
            }
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
            // A panel-tabs marker references its slot exactly like a slot section does.
            foreach (var tab in record.Tabs.Where(t => t.SlotId is not null))
                if (!model.Slots.ContainsKey(tab.SlotId!))
                    throw new InvalidOperationException(
                        $"PAGE001: page '{page.Id}' PanelTabs references undeclared slot '{tab.SlotId}'.");

            foreach (var section in record.AllSections)
            {
                if (section.Kind == RecordSection.FormKind)
                {
                    if (!model.Forms.TryGetValue(section.Id, out var form))
                        throw new InvalidOperationException(
                            $"PAGE001: page '{page.Id}' names unknown form '{section.Id}'.");
                    // Prefill sets the record identity through the SAME-NAMED input field
                    // (docs/32); an operation without it would submit with no record identity —
                    // a broken form that otherwise builds clean (review-round-4 F6).
                    if (model.Operations.TryGetValue(form.OperationId, out var op)
                        && !op.InputFields.Any(f => f.WireName == record.ContextKey))
                        throw new InvalidOperationException(
                            $"PAGE001: page '{page.Id}' record form '{section.Id}' binds operation '{form.OperationId}', which has no '{record.ContextKey}' input — the record key could never prefill.");
                }
                if (section.Kind == RecordSection.GridKind)
                {
                    if (!model.Grids.TryGetValue(section.Id, out var grid))
                        throw new InvalidOperationException(
                            $"PAGE001: page '{page.Id}' record grid '{section.Id}' is not a declared grid.");
                    var gridView = model.Views[grid.ViewId];
                    foreach (var bind in section.Bind ?? [])
                    {
                        // The field side reads a detail-view result field — it must exist or
                        // the child listing would filter on nothing.
                        if (bind.EntityKey is { } entityKey)
                        {
                            if (!model.ExtensibleEntityKeys.Contains(entityKey))
                                throw new InvalidOperationException(
                                    $"PAGE001: page '{page.Id}' record grid '{section.Id}' binds entityRef '{entityKey}', not a known wire entity key.");
                        }
                        else if (!detail.ResultFields.Any(f => f.WireName == bind.Field))
                            throw new InvalidOperationException(
                                $"PAGE001: page '{page.Id}' record grid '{section.Id}' binds from '{bind.Field}', which is not a result field of '{record.DetailViewId}'.");
                        // The param side must be a query field or a declared filter of the
                        // grid's view: the server IGNORES unknown query params, so a typo here
                        // would silently show EVERY row as this record's children.
                        if (!gridView.QueryFields.Any(f => f.WireName == bind.Param)
                            && !gridView.Capabilities.Filterable.Contains(bind.Param))
                            throw new InvalidOperationException(
                                $"PAGE001: page '{page.Id}' record grid '{section.Id}' binds param '{bind.Param}', which is neither a query field nor Filterable on '{grid.ViewId}' — the child listing would show every row, unfiltered.");
                    }
                }
                if (section.Kind == RecordSection.SlotKind && !model.Slots.ContainsKey(section.Id))
                    throw new InvalidOperationException(
                        $"PAGE001: page '{page.Id}' references undeclared slot '{section.Id}'.");
            }
        }

        var referenced = model.Pages.Values.SelectMany(p =>
                p.Sections.Where(s => s.Kind == PageSection.SlotKind).Select(s => s.Id)
                    .Concat(p.Record is { } r
                        ? r.AllSections.Where(s => s.Kind == RecordSection.SlotKind).Select(s => s.Id)
                            .Concat(r.Tabs.Where(t => t.SlotId is not null).Select(t => t.SlotId!))
                        : []))
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
        foreach (var pg in model.Pages.Values) Check("page", pg.Id, pg.Plugin);
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

    /// <summary>Resolves each derivation to the OPERATION that owns it (docs/40) and validates the
    /// ownership invariants, so derivations are found by operation id at runtime rather than by an
    /// input type two operations might share. DER001 orphan (no operation uses the input type),
    /// DER002 ambiguous (several do — the derivation must name its owner via
    /// [ServerDerivation(Operation=...)]), DER003 the named owner is unknown or its Input doesn't
    /// match, DER004 a DependsOn member isn't a field on the owner's input, DER005 duplicate
    /// derivation id within one operation.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<DerivationDefinition>> ResolveDerivationOwnership(
        IReadOnlyDictionary<string, OperationDefinition> operations,
        IReadOnlyList<DerivationDefinition> derivations)
    {
        var byInputType = operations.Values
            .GroupBy(o => o.InputType)
            .ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToList());

        var result = new Dictionary<string, List<DerivationDefinition>>();
        var violations = new List<string>();

        foreach (var d in derivations)
        {
            string? ownerId;
            if (d.DeclaredOperation is { } declared)
            {
                if (!operations.TryGetValue(declared, out var declaredOp))
                { violations.Add($"DER003: derivation '{d.Id}' names unknown operation '{declared}'"); continue; }
                if (declaredOp.InputType != d.InputType)
                { violations.Add($"DER003: derivation '{d.Id}' is assigned to '{declared}' but its input type is not that operation's Input"); continue; }
                ownerId = declared;
            }
            else if (byInputType.TryGetValue(d.InputType, out var owners))
            {
                if (owners.Count > 1)
                { violations.Add($"DER002: derivation '{d.Id}' input type is shared by operations [{string.Join(", ", owners)}] — set [ServerDerivation(Operation=...)] to name its owner"); continue; }
                ownerId = owners[0];
            }
            else
            {
                violations.Add($"DER001: derivation '{d.Id}' input type '{d.InputType.Name}' matches no operation");
                continue;
            }

            var inputFields = operations[ownerId].InputFields.Select(f => f.WireName).ToHashSet();
            foreach (var dep in d.DependsOn.Where(dep => !inputFields.Contains(dep)))
                violations.Add($"DER004: derivation '{d.Id}' depends on '{dep}', not an input field of operation '{ownerId}'");

            if (!result.TryGetValue(ownerId, out var list)) result[ownerId] = list = [];
            if (list.Any(x => x.Id == d.Id))
                violations.Add($"DER005: duplicate derivation id '{d.Id}' on operation '{ownerId}'");
            list.Add(d);
        }

        if (violations.Count > 0)
            throw new InvalidOperationException(
                $"Derivation ownership errors (docs/40): {string.Join("; ", violations)}.");

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DerivationDefinition>)kv.Value);
    }

    /// <summary>MCP001: every operation, form ".resolve" and view projects to a DISTINCT MCP tool
    /// name. Tool names collapse '.' and '-' to '_' (the reverse mapping is a lookup, not string
    /// surgery), so ids differing only in those separators — orders.create-special vs
    /// orders.create_special vs orders-create.special — would share one tool name; tools/list would
    /// advertise duplicates and a call would route to whichever id enumerates first (Sol re-review,
    /// MCP C). Caught at build so an ambiguous agent surface can never ship.</summary>
    private static void VerifyMcpToolNames(TamModel model)
    {
        static string ToolName(string id) => id.Replace('.', '_').Replace('-', '_');
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string toolName, string label)
        {
            if (!byName.TryGetValue(toolName, out var list)) byName[toolName] = list = [];
            list.Add(label);
        }
        foreach (var o in model.Operations.Values) Add(ToolName(o.Id), $"operation '{o.Id}'");
        foreach (var f in model.Forms.Values) Add(ToolName(f.Id) + "_resolve", $"form '{f.Id}'.resolve");
        foreach (var v in model.Views.Values) Add(ToolName("views." + v.Id), $"view '{v.Id}'");

        var collisions = byName.Where(kv => kv.Value.Count > 1)
            .Select(kv => $"'{kv.Key}' ⇐ {string.Join(", ", kv.Value)}")
            .ToList();
        if (collisions.Count > 0)
            throw new InvalidOperationException(
                $"MCP001: MCP tool name collision(s): {string.Join("; ", collisions)}.");
    }

    /// <summary>L10N001: every label key the model references must exist in the default culture.</summary>
    /// <summary>LOOKUP001: a [Lookup] view must exist — a picker over a missing view is a
    /// dead control on every form that uses the type (docs/34 M5).</summary>
    private static void VerifyLookups(TamModel model)
    {
        var fields = model.Operations.Values.SelectMany(o => o.InputFields)
            .Concat(model.Views.Values.SelectMany(v => v.QueryFields));
        foreach (var field in fields)
            if (field.Lookup is { } view && !model.Views.ContainsKey(view))
                throw new InvalidOperationException(
                    $"LOOKUP001: field '{field.WireName}' declares [Lookup(\"{view}\")] but no such view exists.");
    }

    /// <summary>The model's enum registry (docs/34 M6): every enum any field uses, keyed by
    /// kebab wire name — what a plugin form's .EnumOptions("order-type") resolves against.</summary>
    private static Dictionary<string, IReadOnlyList<string>> CollectEnums(
        IReadOnlyDictionary<string, OperationDefinition> operations,
        IReadOnlyDictionary<string, ViewDefinition> views)
    {
        var enums = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var fields = operations.Values.SelectMany(o => o.InputFields)
            .Concat(views.Values.SelectMany(v => v.QueryFields.Concat(v.ResultFields)));
        foreach (var field in fields)
        {
            var t = Nullable.GetUnderlyingType(field.EffectiveType) ?? field.EffectiveType;
            if (t.IsEnum) enums.TryAdd(Naming.Kebab(t.Name), Enum.GetNames(t));
        }
        return enums;
    }

    /// <summary>ENUM001 (docs/34 M6): a form field's .EnumOptions must name an enum the model
    /// actually has — a typo here would otherwise render an empty select and fail silently,
    /// the exact wart the seam exists to close.</summary>
    private static void VerifyEnumOptions(TamModel model)
    {
        foreach (var form in model.Forms.Values)
            foreach (var config in form.Fields)
                if (config.OptionsFromEnum is { } key && !model.Enums.ContainsKey(key))
                    throw new InvalidOperationException(
                        $"ENUM001: form '{form.Id}' field '{config.WireName}' references enum options " +
                        $"'{key}' but the model has no such enum. Known: {string.Join(", ", model.Enums.Keys.OrderBy(k => k))}.");
    }

    /// <summary>FORM001 (docs/40, Sol re-review round 6, F2): a form's RequiredWhen may not depend
    /// on a CHANGE-SET field. Submit is sparse for edit forms — an untouched Change&lt;T&gt; field is
    /// omitted from the body entirely — so a requiredness predicate keyed off one would read null at
    /// submit even when the record holds a value, quietly demanding (or waiving) a field on the
    /// wrong basis. Requiredness must key off fields the wire always carries. Caught at build so the
    /// contract can never ship this mismatch.</summary>
    private static void VerifyFormPredicates(TamModel model)
    {
        foreach (var form in model.Forms.Values)
        {
            if (!model.Operations.TryGetValue(form.OperationId, out var operation)) continue;
            var changeFields = operation.InputFields
                .Where(f => f.IsChangeSet).Select(f => f.WireName)
                .ToHashSet(StringComparer.Ordinal);
            if (changeFields.Count == 0) continue;
            foreach (var config in form.Fields)
            {
                if (config.RequiredWhen is not { } predicate) continue;
                var offending = predicate.Fields().FirstOrDefault(changeFields.Contains);
                if (offending is not null)
                    throw new InvalidOperationException(
                        $"FORM001: form '{form.Id}' field '{config.WireName}' has a RequiredWhen that "
                        + $"references change-set field '{offending}'. Submit omits untouched change "
                        + "fields, so requiredness read from one is unreliable — key RequiredWhen off "
                        + "a field the wire always carries, or move the rule into a [ServerDerivation]. "
                        + "A derivation that depends on an untouched field must load the current aggregate "
                        + "and overlay the sparse patch (docs/40) — the deserialized submit input alone is "
                        + "not complete effective state.");
            }
        }
    }

    /// <summary>L10N005 (WARNING, docs/34 M5): DIFFERENT semantic wrapper types claiming the
    /// same convention-derived label key — the exact trap where Project.Number silently wore
    /// orders' "Order number" text. Plain string/enum members sharing generic keys ("Name",
    /// "Status") stay silent: one text genuinely serves them. Silence a true positive with a
    /// [LabelKey] on one of the wrapper types.</summary>
    private static List<string> CollectLabelWarnings(
        IReadOnlyDictionary<string, OperationDefinition> operations,
        IReadOnlyDictionary<string, ViewDefinition> views)
    {
        var claims = new Dictionary<string, HashSet<Type>>(StringComparer.Ordinal);
        void Claim(FieldModel field)
        {
            // Only convention-derived keys on WRAPPER types — an explicit [LabelKey] is a
            // deliberate share, and primitive members sharing "Name" is the convention working.
            if (field.LabelKey != LabelKeys.Field(field.MemberName)) return;
            var type = Nullable.GetUnderlyingType(field.EffectiveType) ?? field.EffectiveType;
            if (!ValueWrapper.IsWrapper(type)) return;
            (claims.TryGetValue(field.LabelKey, out var set)
                ? set : claims[field.LabelKey] = []).Add(type);
        }
        foreach (var op in operations.Values)
            foreach (var f in op.InputFields) Claim(f);
        foreach (var view in views.Values)
            foreach (var f in view.ResultFields) Claim(f);

        return claims.Where(kv => kv.Value.Count > 1)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"L10N005: label key '{kv.Key}' is convention-claimed by different "
                + $"semantic types ({string.Join(", ", kv.Value.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal))}) "
                + "— one catalog text serves them all; if the text is type-specific, give one "
                + "a [LabelKey] on its wrapper type.")
            .ToList();
    }

    private static void VerifyLocalization(TamModel model, LocaleCatalogs catalogs)
    {
        static IEnumerable<string> NavLabels(NavNode node) =>
            new[] { node.LabelKey }.Concat(node.Children.SelectMany(NavLabels));
        var required = model.Operations.Values.SelectMany(o => o.InputFields.Select(f => f.LabelKey))
            // Output fields ride the manifest with label keys too (agents/result rendering) —
            // ungated they were the one hole in "no missing text in the default culture"
            // (review round 4: a docs-only implementer shipped one and nothing failed).
            .Concat(model.Operations.Values.SelectMany(o => o.OutputType is { } output
                ? FieldModel.FromRecord(output).Select(f => f.LabelKey)
                : []))
            .Concat(model.Operations.Values.Select(o => o.TitleKey))
            .Concat(model.Views.Values.SelectMany(v => v.ResultFields.Select(f => f.LabelKey)))
            .Concat(model.Plugins.Values.Select(p => p.TitleKey))
            .Concat(model.Nav.Values.SelectMany(nodes => nodes.SelectMany(NavLabels)))
            // Page section headings (docs/34 M6) are product surface like any label.
            .Concat(model.Pages.Values.SelectMany(p => p.Sections
                .Where(sec => sec.HeadingKey is not null).Select(sec => sec.HeadingKey!)))
            // Record tab headings likewise (docs/32 record tabs); the implicit tab has none.
            .Concat(model.Pages.Values.SelectMany(p => p.Record?.Tabs
                .Where(t => t.HeadingKey is not null).Select(t => t.HeadingKey!) ?? []));

        var missing = catalogs.MissingKeys(required, catalogs.DefaultCulture);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"L10N001: {missing.Count} key(s) missing in default culture '{catalogs.DefaultCulture}': " +
                string.Join(", ", missing));   // ALL of them — nobody fixes keys one crash at a time
        }
    }

    private sealed class LocaleCatalogsBuilder
    {
        public List<string> Directories { get; } = [];

        public List<(string Culture, IReadOnlyDictionary<string, string> Entries)> Defaults { get; } = [];
    }
}
