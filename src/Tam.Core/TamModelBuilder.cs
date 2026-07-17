using System.Reflection;

namespace Tam;

public sealed partial class TamModelBuilder

{
    private readonly List<(Type Type, string? Plugin)> operationTypes = [];
    private readonly List<(Type Type, string? Plugin)> viewTypes = [];
    private readonly List<Type> derivationTypes = [];
    private readonly Dictionary<string, (Type Input, object Builder, string OperationId, string? Plugin)> forms = [];
    private readonly Dictionary<string, (object Builder, string ViewId, string? Plugin)> grids = [];
    private readonly Dictionary<string, PluginDefinition> plugins = [];
    private readonly Dictionary<string, PackageDefinition> packages = [];
    private readonly List<(string EntityKey, string Key, string Type, bool Required, int? MaxLength, IReadOnlyList<string>? Options, bool ReadOnly, string Plugin)> packagedFields = [];
    private readonly List<GateDefinition> gates = [];
    private readonly List<GridActionContribution> gridActions = [];
    private readonly List<ViewRequirement> viewRequirements = [];
    private readonly Dictionary<string, SlotDefinition> slots = [];
    private readonly Dictionary<string, PageDefinition> pages = [];
    private readonly List<PanelContribution> panels = [];
    private readonly Dictionary<string, EventDeclaration> events = [];
    private readonly List<EventRequirement> eventRequirements = [];
    private readonly Dictionary<string, NavTreeBuilder> navTrees = [];
    private readonly List<NavContribution> navContributions = [];
    private readonly List<SubscriberDefinition> subscribers = [];
    private readonly List<(string Id, string OperationId, IntegrationKeySelector Key, IntegrationRowMapper Map, string Plugin)> integrations = [];
    private readonly List<(string Id, IntegrationTrigger Trigger, OutboundIntegrationHandler Handler, string Plugin)> outboundIntegrations = [];
    private readonly LocaleCatalogsBuilder locales = new();
    private string defaultCulture = "en";
    private string? currentPlugin;

    /// <summary>PLG005's other half: the builder methods that shape the HOST (registering more
    /// plugins, cultures, layout, building) are unreachable from a plugin's Configure — without
    /// this, a plugin calling AddPlugin/AddPackage would reset the ambient plugin tag and every
    /// later registration would escape PLG001's namespace enforcement entirely.</summary>
    private void HostOnly(string method)
    {
        if (currentPlugin is not null)
            throw new InvalidOperationException(
                $"PLG005: {method} is the host's — a plugin/package ('{currentPlugin}') cannot call it from Configure.");
    }

    public TamModelBuilder DefaultCulture(string culture)
    {
        HostOnly("DefaultCulture");
        defaultCulture = culture;
        return this;
    }

    public TamModelBuilder Locales(string directory)
    {
        HostOnly("Locales");
        locales.Directories.Add(directory);
        return this;
    }

    /// <summary>Programmatic catalog entries (framework defaults from embedded resources).
    /// Applied before directories, so application locale files override them.</summary>
    public TamModelBuilder LocaleDefaults(string culture, IReadOnlyDictionary<string, string> entries)
    {
        locales.Defaults.Add((culture, entries));
        return this;
    }

    /// <summary>Explicit registration, inspectable: scans one assembly for [Operation]/[View]/[ServerDerivation].
    /// Prefer the generated <c>AddDiscovered()</c> (Tam.Compiler source generator) — same result, no runtime scan.</summary>
    public TamModelBuilder AddAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<OperationAttribute>() is not null) operationTypes.Add((type, currentPlugin));
            if (type.GetCustomAttribute<ViewAttribute>() is not null) viewTypes.Add((type, currentPlugin));
            if (type.IsAbstract && type.IsSealed &&
                type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Any(m => m.GetCustomAttribute<ServerDerivationAttribute>() is not null))
            {
                derivationTypes.Add(type);
            }
        }
        return this;
    }

    /// <summary>
    /// Registers a compiled plugin (docs/22): reviewed code bound at build time, activated per
    /// tenant at runtime. Everything the plugin's Configure registers is tagged with its id, so
    /// the effective manifest can omit it and PLG001 can enforce the namespace prefix.
    /// </summary>
    public TamModelBuilder AddPlugin<TPlugin>() where TPlugin : ITamPlugin, new()
    {
        HostOnly("AddPlugin");
        var attribute = typeof(TPlugin).GetCustomAttribute<TamPluginAttribute>()
            ?? throw new InvalidOperationException(
                $"PLG000: plugin type '{typeof(TPlugin).Name}' lacks [TamPlugin(\"id\")].");
        if (!System.Text.RegularExpressions.Regex.IsMatch(attribute.Id, "^[a-z][a-z0-9]*$"))
            throw new InvalidOperationException(
                $"PLG000: plugin id '{attribute.Id}' must match ^[a-z][a-z0-9]*$ — it is a permanent wire prefix.");
        if (plugins.ContainsKey(attribute.Id) || packages.ContainsKey(attribute.Id))
            throw new InvalidOperationException($"PLG003: plugin id '{attribute.Id}' registered twice.");

        plugins[attribute.Id] = new PluginDefinition(attribute.Id);
        currentPlugin = attribute.Id;
        try
        {
            var builder = new PluginBuilder(attribute.Id, typeof(TPlugin).Assembly, this);
            builder.EmbeddedLocaleDefaults();   // locales/*.json ship as defaults, no ceremony
            new TPlugin().Configure(builder);
        }
        finally
        {
            currentPlugin = null;
        }
        return this;
    }

    /// <summary>
    /// Registers a FRAMEWORK PACKAGE (docs/22, the framework-trust tier): same authoring surface
    /// as a plugin, but always active for every tenant and validated against its CLAIMED wire
    /// prefixes instead of an id namespace — framework wire names ("users.invite") are live and
    /// permanent, so a package owns them rather than re-prefixing.
    /// </summary>
    public TamModelBuilder AddPackage<TPackage>() where TPackage : ITamPlugin, new()
    {
        HostOnly("AddPackage");
        var attribute = typeof(TPackage).GetCustomAttribute<TamPackageAttribute>()
            ?? throw new InvalidOperationException(
                $"PKG000: package type '{typeof(TPackage).Name}' lacks [TamPackage(\"id\", prefixes)].");
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                attribute.Id, "^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)*$"))
            throw new InvalidOperationException(
                $"PKG000: package id '{attribute.Id}' must be dot-separated lowercase segments.");
        if (attribute.Prefixes.Length == 0)
            throw new InvalidOperationException(
                $"PKG000: package '{attribute.Id}' claims no wire prefixes — every contribution would be a violation.");
        if (packages.ContainsKey(attribute.Id) || plugins.ContainsKey(attribute.Id))
            throw new InvalidOperationException($"PLG003: id '{attribute.Id}' registered twice.");

        packages[attribute.Id] = new PackageDefinition(attribute.Id, attribute.Prefixes);
        currentPlugin = attribute.Id;
        try
        {
            var builder = new PluginBuilder(attribute.Id, typeof(TPackage).Assembly, this);
            builder.EmbeddedLocaleDefaults();   // locales/*.json ship as defaults, no ceremony
            new TPackage().Configure(builder);
        }
        finally
        {
            currentPlugin = null;
        }
        return this;
    }

    /// <summary>
    /// Declares a surface's navigation tree (docs/30). LAYOUT IS THE HOST'S: packages/plugins
    /// contribute content + suggestions through <see cref="PluginBuilder.Nav"/>, never layout.
    /// </summary>
    public TamModelBuilder Nav(string surface, Action<NavTreeBuilder> configure)
    {
        if (currentPlugin is not null)
            throw new InvalidOperationException(
                "NAV000: layout is the host's — plugins/packages contribute via PluginBuilder.Nav.");
        if (!navTrees.TryGetValue(surface, out var tree))
            navTrees[surface] = tree = new NavTreeBuilder();
        configure(tree);
        return this;
    }

    internal void NavContribute(NavContribution contribution) =>
        navContributions.Add(contribution);

    public TamModelBuilder AddOperationType(Type type)
    {
        operationTypes.Add((type, currentPlugin));
        return this;
    }

    /// <summary>Registers a gate class by its [Gate]/[GateAll] attribute — the add-by-type
    /// substrate the generated AddDiscovered() emits (review round 4: registration lives ON
    /// the behavior). Plugin scope only, like the fluent twin.</summary>
    public TamModelBuilder AddGateType(Type type)
    {
        var all = type.GetCustomAttribute<GateAllAttribute>();
        var one = type.GetCustomAttribute<GateAttribute>();
        if (all is null && one is null)
            throw new InvalidOperationException(
                $"PLG012: '{type.Name}' has no [Gate]/[GateAll] attribute to register from.");
        if (!typeof(IOperationGate).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"PLG012: '{type.Name}' is [Gate]-attributed but does not implement IOperationGate.");
        if (all is not null) Gate(GateDefinition.Wildcard, type, all.Pure);
        else Gate(one!.OperationId, type, one.Pure);
        return this;
    }

    /// <summary>Registers an effect subscriber by its [OnEffect] attribute(s) — one
    /// subscription per attribute. Plugin scope only; PLG009 verifies the targets at Build().</summary>
    public TamModelBuilder AddSubscriberType(Type type)
    {
        var attributes = type.GetCustomAttributes<OnEffectAttribute>().ToList();
        if (attributes.Count == 0)
            throw new InvalidOperationException(
                $"PLG012: '{type.Name}' has no [OnEffect] attribute to register from.");
        if (!typeof(IEffectHandler).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"PLG012: '{type.Name}' is [OnEffect]-attributed but does not implement IEffectHandler.");
        foreach (var attribute in attributes) OnEffect(attribute.EventType, type);
        return this;
    }

    public TamModelBuilder AddViewType(Type type)
    {
        viewTypes.Add((type, currentPlugin));
        return this;
    }

    public TamModelBuilder AddDerivationHost(Type type)
    {
        derivationTypes.Add(type);
        return this;
    }

    public TamModelBuilder Form<TInput>(string id, string operationId,
        Action<FormBuilder<TInput>>? configure = null)
    {
        var builder = new FormBuilder<TInput>();
        configure?.Invoke(builder);
        forms[id] = (typeof(TInput), builder, operationId, currentPlugin);
        return this;
    }

    /// <summary>Binds a grid. WITHOUT <paramref name="configure"/>, every view result field
    /// becomes a column in record declaration order — minus the conventions (id, version) that
    /// are row plumbing, not display; configure only to subset, reorder, or add actions.</summary>
    public TamModelBuilder Grid<TResult>(string id, string viewId,
        Action<GridBuilder<TResult>>? configure = null)
    {
        var builder = new GridBuilder<TResult>();
        configure?.Invoke(builder);
        grids[id] = (builder, viewId, currentPlugin);
        return this;
    }

    public TamModel Build()
    {
        HostOnly("Build");
        var catalogs = new LocaleCatalogs(defaultCulture);
        foreach (var (culture, entries) in locales.Defaults) catalogs.Add(culture, entries);
        foreach (var dir in locales.Directories) catalogs.AddFromDirectory(dir);

        var operations = operationTypes
            .Select(x => OperationDefinition.From(x.Type) with { Plugin = x.Plugin })
            .ToDictionary(o => o.Id);
        var views = viewTypes
            .Select(x => ViewDefinition.From(x.Type) with { Plugin = x.Plugin })
            .ToDictionary(v => v.Id);
        var derivations = derivationTypes.SelectMany(DerivationDefinition.FromType).ToList();

        var formDefs = new Dictionary<string, FormDefinition>();
        foreach (var (id, (input, builder, operationId, plugin)) in forms)
        {
            if (!operations.TryGetValue(operationId, out var op))
                throw new InvalidOperationException($"FORM003: form '{id}' references unknown operation '{operationId}'.");
            if (op.InputType != input)
                throw new InvalidOperationException($"FORM004: form '{id}' input type mismatch with operation '{operationId}'.");
            var build = builder.GetType().GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var formDef = (FormDefinition)build.Invoke(builder, [id, operationId, null])! with { Plugin = plugin };
            if (formDef.Fields.Count == 0)
            {
                // Convention default: the record IS the form (docs/32).
                formDef = formDef with
                {
                    Fields = op.InputFields
                        .Select(fld => new FormFieldConfig(fld.WireName, null, null, null,
                            DependentValuePolicy.RecomputeIfUntouched))
                        .ToList(),
                };
            }
            formDefs[id] = formDef;
        }

        var gridDefs = new Dictionary<string, GridDefinition>();
        foreach (var (id, (builder, viewId, plugin)) in grids)
        {
            if (!views.TryGetValue(viewId, out var view))
                throw new InvalidOperationException($"GRID001: grid '{id}' references unknown view '{viewId}'.");
            var build = builder.GetType().GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var def = (GridDefinition)build.Invoke(builder, [id, viewId])! with { Plugin = plugin };
            if (def.Columns.Count == 0)
            {
                // Convention default: every result field is a column, in declaration order —
                // minus row plumbing (id, version) and object-shaped fields (extensions render
                // via IncludeExtensions, never as one column). Configure only to deviate.
                def = def with
                {
                    Columns = view.ResultFields
                        .Where(fld => fld.WireName is not ("id" or "version")
                            && fld.Semantic.WireKind != "object")
                        .Select(fld => fld.WireName)
                        .ToList(),
                };
            }

            foreach (var action in def.Actions.Select(a => a.Operation))
                if (!operations.ContainsKey(action))
                    throw new InvalidOperationException($"GRID002: grid '{id}' action references unknown operation '{action}'.");

            var resultFields = view.ResultFields.Select(f => f.WireName).ToHashSet();
            foreach (var column in def.Columns)
                if (!resultFields.Contains(column))
                    throw new InvalidOperationException($"VIEW001: grid '{id}' declares column '{column}' not present on view '{viewId}'.");

            gridDefs[id] = def;
        }

        // Packaged fields materialize against the finished catalogs: their labels are plugin
        // locale defaults under "ext.{key}" — data in catalogs, never display text in code.
        var extensibleKeys = operations.Values.Select(o => o.ExtensibleEntity)
            .Concat(views.Values.Select(v => v.ExtensibleEntity))
            .Where(t => t is not null).Select(t => TamModel.EntityKey(t!)).ToHashSet();
        var duplicatePackaged = packagedFields.GroupBy(r => (r.EntityKey, r.Key))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicatePackaged is not null)
            throw new InvalidOperationException(
                $"PLG004: packaged field '{duplicatePackaged.Key.Key}' on '{duplicatePackaged.Key.EntityKey}' is declared more than once.");
        var packaged = packagedFields.Select(r =>
        {
            if (!extensibleKeys.Contains(r.EntityKey))
                throw new InvalidOperationException(
                    $"PLG004: packaged field '{r.Key}' targets unknown extensible entity '{r.EntityKey}'.");
            if (!SemanticTypes.ByKey.ContainsKey(r.Type))
                throw new InvalidOperationException(
                    $"PLG004: packaged field '{r.Key}' has unknown type '{r.Type}'.");
            var labels = catalogs.Cultures
                .Select(c => (Culture: c, Text: catalogs.Lookup(LabelKeys.Extension(r.Key), c)))
                .Where(x => x.Text is not null)
                .ToDictionary(x => x.Culture, x => x.Text!);
            if (!labels.ContainsKey(defaultCulture))
                throw new InvalidOperationException(
                    $"L10N001: packaged field '{r.Key}' has no 'ext.{r.Key}' label in default culture '{defaultCulture}'.");
            return new PackagedFieldDefinition(r.Plugin, r.EntityKey, new ExtensionFieldSpec(
                r.Key, r.EntityKey, r.Type, r.Required, r.MaxLength,
                labels, null, r.Options, ExtensionFieldState.Active)
            { ReadOnly = r.ReadOnly });
        }).ToList();

        // A wildcard gate (docs/28 approvals seam 1) names no operation by design — its target
        // set is tenant config, resolved at execution time — so only concrete ids are checked.
        foreach (var gate in gates)
            if (gate.OperationId != GateDefinition.Wildcard && !operations.ContainsKey(gate.OperationId))
                throw new InvalidOperationException(
                    $"PLG002: plugin '{gate.PluginId}' gates unknown operation '{gate.OperationId}'.");
        foreach (var integration in integrations)
        {
            if (!operations.ContainsKey(integration.OperationId))
                throw new InvalidOperationException(
                    $"INT002: integration '{integration.Id}' targets unknown operation '{integration.OperationId}'.");
            if (!integration.Id.StartsWith(integration.Plugin + ".", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"PLG001: integration '{integration.Id}' is not under '{integration.Plugin}.'.");
        }
        foreach (var outbound in outboundIntegrations)
            if (!outbound.Id.StartsWith(outbound.Plugin + ".", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"PLG001: outbound integration '{outbound.Id}' is not under '{outbound.Plugin}.'.");
        // PLG002 guards ACCIDENTAL double registration — the same handler twice. Distinct
        // handlers on one target are deliberate composition (tam.rules runs a pure finding
        // gate AND a transactional action gate over '*' — docs/22 action catalog).
        var duplicateGate = gates.GroupBy(g => (g.OperationId, g.PluginId, g.HandlerType))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateGate is not null)
            throw new InvalidOperationException(
                $"PLG002: plugin '{duplicateGate.Key.PluginId}' gates '{duplicateGate.Key.OperationId}' "
                + $"with '{duplicateGate.Key.HandlerType.Name}' more than once.");

        var mergedNav = MergeNav(gridDefs);

        // Placement IS declaration (docs/34 M5 fix 9): a slot placed on a declared page
        // auto-declares itself — a record slot provides the record's context key, a
        // page-level slot provides none. Standalone model.Slot() remains for external
        // slots placed by app React, and for overriding the auto-derived context keys.
        foreach (var page in pages.Values)
        {
            if (page.Record is { } record)
                foreach (var section in record.Sections.Where(x => x.Kind == RecordSection.SlotKind))
                    slots.TryAdd(section.Id, new SlotDefinition(section.Id, [record.ContextKey]));
            foreach (var section in page.Sections.Where(x => x.Kind == PageSection.SlotKind))
                slots.TryAdd(section.Id, new SlotDefinition(section.Id, []));
        }

        var model = new TamModel
        {
            Nav = mergedNav,
            DefaultCulture = defaultCulture,
            Locales = catalogs,
            Operations = operations,
            Views = views,
            Derivations = derivations,
            Forms = formDefs,
            Grids = gridDefs,
            Plugins = plugins,
            Packages = packages,
            PackagedFields = packaged,
            ExtensibleEntityKeys = extensibleKeys,
            Pages = pages,
            Slots = slots,
            Warnings = CollectLabelWarnings(operations, views),
            Enums = CollectEnums(operations, views),
            Panels = panels.GroupBy(p => p.SlotId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<PanelContribution>)g.ToList()),
            Events = events,
            GridActions = gridActions.GroupBy(a => a.GridId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<GridActionContribution>)g.ToList()),
            ViewRequirements = viewRequirements,
            Gates = gates.GroupBy(g => g.OperationId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<GateDefinition>)g.ToList()),
            Subscribers = subscribers,
            Integrations = integrations.ToDictionary(
                i => i.Id,
                i => new PluginIntegrationDefinition(i.Id, i.Plugin, i.OperationId, i.Key, i.Map)),
            OutboundIntegrations = outboundIntegrations.ToDictionary(
                i => i.Id,
                i => new OutboundIntegrationDefinition(i.Id, i.Plugin, i.Trigger, i.Handler)),
        };

        VerifyPluginNamespaces(model);
        VerifyNav(model);
        VerifySubtreeViews(model);
        VerifyContributions(model);
        VerifySlotsAndEvents(model, eventRequirements);
        VerifyPages(model);
        VerifyLookups(model);
        VerifyEnumOptions(model);
        VerifyLocalization(model, catalogs);
        return model;
    }
}
