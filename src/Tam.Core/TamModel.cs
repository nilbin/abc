using System.Reflection;

namespace Tam;

/// <summary>
/// The application model, built once at startup from explicitly registered assemblies and bindings.
/// This is the runtime stand-in for the compiled manifest of docs/12 — same shape, same consumers;
/// the Roslyn source-generator packaging is a later phase (see STATUS.md).
/// </summary>
public sealed class TamModel
{
    public required string DefaultCulture { get; init; }

    public required LocaleCatalogs Locales { get; init; }

    public required IReadOnlyDictionary<string, OperationDefinition> Operations { get; init; }

    public required IReadOnlyDictionary<string, ViewDefinition> Views { get; init; }

    public required IReadOnlyList<DerivationDefinition> Derivations { get; init; }

    public required IReadOnlyDictionary<string, FormDefinition> Forms { get; init; }

    public required IReadOnlyDictionary<string, GridDefinition> Grids { get; init; }

    /// <summary>Compiled plugins (docs/22). Which are ACTIVE is tenant data, not model data.</summary>
    public IReadOnlyDictionary<string, PluginDefinition> Plugins { get; init; } =
        new Dictionary<string, PluginDefinition>();

    /// <summary>Plugin-packaged extension fields on host entities, by entity key (docs/22 P2).</summary>
    public IReadOnlyList<PackagedFieldDefinition> PackagedFields { get; init; } = [];

    /// <summary>Wire keys of entities that accept extensions — the registry validates
    /// tenant/package field definitions against this set (EXT007).</summary>
    public IReadOnlySet<string> ExtensibleEntityKeys { get; init; } = new HashSet<string>();

    /// <summary>Plugin gates by target operation id (docs/22 P2).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GateDefinition>> Gates { get; init; } =
        new Dictionary<string, IReadOnlyList<GateDefinition>>();

    /// <summary>Plugin effect subscribers by event type (docs/22 P2).</summary>
    public IReadOnlyList<SubscriberDefinition> Subscribers { get; init; } = [];

    public IReadOnlyList<string> Permissions =>
        Operations.Values.Select(o => o.Permission)
            .Concat(Views.Values.Select(v => v.Permission))
            .Distinct().Order().ToList();

    public IEnumerable<DerivationDefinition> DerivationsFor(Type inputType) =>
        Derivations.Where(d => d.InputType == inputType);

    /// <summary>Stable key for an extensible entity CLR type: "orders.order" style from namespace-less name.</summary>
    public static string EntityKey(Type entity) => Naming.Kebab(entity.Name);
}

public sealed class TamModelBuilder
{
    private readonly List<(Type Type, string? Plugin)> operationTypes = [];
    private readonly List<(Type Type, string? Plugin)> viewTypes = [];
    private readonly List<Type> derivationTypes = [];
    private readonly Dictionary<string, (Type Input, object Builder, string OperationId, string? Plugin)> forms = [];
    private readonly Dictionary<string, (object Builder, string ViewId, string? Plugin)> grids = [];
    private readonly Dictionary<string, PluginDefinition> plugins = [];
    private readonly List<(string EntityKey, string Key, string Type, bool Required, int? MaxLength, IReadOnlyList<string>? Options, string Plugin)> packagedFields = [];
    private readonly List<GateDefinition> gates = [];
    private readonly List<SubscriberDefinition> subscribers = [];
    private readonly LocaleCatalogsBuilder locales = new();
    private string defaultCulture = "en";
    private string? currentPlugin;

    public TamModelBuilder DefaultCulture(string culture)
    {
        defaultCulture = culture;
        return this;
    }

    public TamModelBuilder Locales(string directory)
    {
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
        var attribute = typeof(TPlugin).GetCustomAttribute<TamPluginAttribute>()
            ?? throw new InvalidOperationException(
                $"PLG000: plugin type '{typeof(TPlugin).Name}' lacks [TamPlugin(\"id\")].");
        if (!System.Text.RegularExpressions.Regex.IsMatch(attribute.Id, "^[a-z][a-z0-9]*$"))
            throw new InvalidOperationException(
                $"PLG000: plugin id '{attribute.Id}' must match ^[a-z][a-z0-9]*$ — it is a permanent wire prefix.");
        if (plugins.ContainsKey(attribute.Id))
            throw new InvalidOperationException($"PLG003: plugin id '{attribute.Id}' registered twice.");

        plugins[attribute.Id] = new PluginDefinition(attribute.Id);
        currentPlugin = attribute.Id;
        try
        {
            new TPlugin().Configure(new PluginBuilder(attribute.Id, this));
        }
        finally
        {
            currentPlugin = null;
        }
        return this;
    }

    public TamModelBuilder AddOperationType(Type type)
    {
        operationTypes.Add((type, currentPlugin));
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

    internal void PackagedField(
        string entityKey, string key, string type, bool required, int? maxLength, IReadOnlyList<string>? options)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: packaged fields can only be declared by a plugin.");
        packagedFields.Add((entityKey, key, type, required, maxLength, options, currentPlugin));
    }

    internal void Gate(string operationId, OperationGate gate)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: gates can only be declared by a plugin.");
        gates.Add(new GateDefinition(operationId, currentPlugin, gate));
    }

    internal void OnEffect(string eventType, EffectSubscriber handler)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: effect subscribers can only be declared by a plugin.");
        subscribers.Add(new SubscriberDefinition(eventType, currentPlugin, handler));
    }

    public TamModelBuilder Form<TInput>(string id, string operationId, Action<FormBuilder<TInput>> configure)
    {
        var builder = new FormBuilder<TInput>();
        configure(builder);
        forms[id] = (typeof(TInput), builder, operationId, currentPlugin);
        return this;
    }

    public TamModelBuilder Grid<TResult>(string id, string viewId, Action<GridBuilder<TResult>> configure)
    {
        var builder = new GridBuilder<TResult>();
        configure(builder);
        grids[id] = (builder, viewId, currentPlugin);
        return this;
    }

    public TamModel Build()
    {
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
            formDefs[id] = (FormDefinition)build.Invoke(builder, [id, operationId, null])! with { Plugin = plugin };
        }

        var gridDefs = new Dictionary<string, GridDefinition>();
        foreach (var (id, (builder, viewId, plugin)) in grids)
        {
            if (!views.TryGetValue(viewId, out var view))
                throw new InvalidOperationException($"GRID001: grid '{id}' references unknown view '{viewId}'.");
            var build = builder.GetType().GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var def = (GridDefinition)build.Invoke(builder, [id, viewId])! with { Plugin = plugin };

            foreach (var action in def.RowActions.Concat(def.ToolbarActions))
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
                .Select(c => (Culture: c, Text: catalogs.Lookup($"ext.{r.Key}", c)))
                .Where(x => x.Text is not null)
                .ToDictionary(x => x.Culture, x => x.Text!);
            if (!labels.ContainsKey(defaultCulture))
                throw new InvalidOperationException(
                    $"L10N001: packaged field '{r.Key}' has no 'ext.{r.Key}' label in default culture '{defaultCulture}'.");
            return new PackagedFieldDefinition(r.Plugin, r.EntityKey, new ExtensionFieldSpec(
                r.Key, r.EntityKey, r.Type, r.Required, r.MaxLength,
                labels, null, r.Options, ExtensionFieldState.Active));
        }).ToList();

        foreach (var gate in gates)
            if (!operations.ContainsKey(gate.OperationId))
                throw new InvalidOperationException(
                    $"PLG002: plugin '{gate.PluginId}' gates unknown operation '{gate.OperationId}'.");
        var duplicateGate = gates.GroupBy(g => (g.OperationId, g.PluginId))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateGate is not null)
            throw new InvalidOperationException(
                $"PLG002: plugin '{duplicateGate.Key.PluginId}' gates '{duplicateGate.Key.OperationId}' more than once.");

        var model = new TamModel
        {
            DefaultCulture = defaultCulture,
            Locales = catalogs,
            Operations = operations,
            Views = views,
            Derivations = derivations,
            Forms = formDefs,
            Grids = gridDefs,
            Plugins = plugins,
            PackagedFields = packaged,
            ExtensibleEntityKeys = extensibleKeys,
            Gates = gates.GroupBy(g => g.OperationId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<GateDefinition>)g.ToList()),
            Subscribers = subscribers,
        };

        VerifyPluginNamespaces(model);
        VerifyLocalization(model, catalogs);
        return model;
    }

    /// <summary>
    /// PLG001: everything a plugin contributes lives under its permanent id prefix — operation,
    /// view, form and grid ids, and the permissions they declare. Collisions between plugins and
    /// host (or each other) are thereby impossible by construction.
    /// </summary>
    private static void VerifyPluginNamespaces(TamModel model)
    {
        var violations = new List<string>();

        void Check(string kind, string id, string? plugin, string? permission = null)
        {
            if (plugin is null) return;
            var prefix = plugin + ".";
            if (!id.StartsWith(prefix, StringComparison.Ordinal))
                violations.Add($"{kind} '{id}' is not under '{prefix}'");
            if (permission is not null && !permission.StartsWith(prefix, StringComparison.Ordinal))
                violations.Add($"{kind} '{id}' permission '{permission}' is not under '{prefix}'");
        }

        foreach (var o in model.Operations.Values) Check("operation", o.Id, o.Plugin, o.Permission);
        foreach (var v in model.Views.Values) Check("view", v.Id, v.Plugin, v.Permission);
        foreach (var f in model.Forms.Values) Check("form", f.Id, f.Plugin);
        foreach (var g in model.Grids.Values) Check("grid", g.Id, g.Plugin);

        if (violations.Count > 0)
            throw new InvalidOperationException(
                $"PLG001: plugin contributions outside their namespace: {string.Join("; ", violations)}.");
    }

    /// <summary>L10N001: every label key the model references must exist in the default culture.</summary>
    private static void VerifyLocalization(TamModel model, LocaleCatalogs catalogs)
    {
        var required = model.Operations.Values.SelectMany(o => o.InputFields.Select(f => f.LabelKey))
            .Concat(model.Operations.Values.Select(o => o.TitleKey))
            .Concat(model.Views.Values.SelectMany(v => v.ResultFields.Select(f => f.LabelKey)))
            .Concat(model.Plugins.Values.Select(p => p.TitleKey));

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
