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
    private readonly List<Type> operationTypes = [];
    private readonly List<Type> viewTypes = [];
    private readonly List<Type> derivationTypes = [];
    private readonly Dictionary<string, (Type Input, object Builder, string OperationId)> forms = [];
    private readonly Dictionary<string, (object Builder, string ViewId)> grids = [];
    private readonly LocaleCatalogsBuilder locales = new();
    private string defaultCulture = "en";

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
            if (type.GetCustomAttribute<OperationAttribute>() is not null) operationTypes.Add(type);
            if (type.GetCustomAttribute<ViewAttribute>() is not null) viewTypes.Add(type);
            if (type.IsAbstract && type.IsSealed &&
                type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Any(m => m.GetCustomAttribute<ServerDerivationAttribute>() is not null))
            {
                derivationTypes.Add(type);
            }
        }
        return this;
    }

    public TamModelBuilder AddOperationType(Type type)
    {
        operationTypes.Add(type);
        return this;
    }

    public TamModelBuilder AddViewType(Type type)
    {
        viewTypes.Add(type);
        return this;
    }

    public TamModelBuilder AddDerivationHost(Type type)
    {
        derivationTypes.Add(type);
        return this;
    }

    public TamModelBuilder Form<TInput>(string id, string operationId, Action<FormBuilder<TInput>> configure)
    {
        var builder = new FormBuilder<TInput>();
        configure(builder);
        forms[id] = (typeof(TInput), builder, operationId);
        return this;
    }

    public TamModelBuilder Grid<TResult>(string id, string viewId, Action<GridBuilder<TResult>> configure)
    {
        var builder = new GridBuilder<TResult>();
        configure(builder);
        grids[id] = (builder, viewId);
        return this;
    }

    public TamModel Build()
    {
        var catalogs = new LocaleCatalogs(defaultCulture);
        foreach (var (culture, entries) in locales.Defaults) catalogs.Add(culture, entries);
        foreach (var dir in locales.Directories) catalogs.AddFromDirectory(dir);

        var operations = operationTypes.Select(OperationDefinition.From).ToDictionary(o => o.Id);
        var views = viewTypes.Select(ViewDefinition.From).ToDictionary(v => v.Id);
        var derivations = derivationTypes.SelectMany(DerivationDefinition.FromType).ToList();

        var formDefs = new Dictionary<string, FormDefinition>();
        foreach (var (id, (input, builder, operationId)) in forms)
        {
            if (!operations.TryGetValue(operationId, out var op))
                throw new InvalidOperationException($"FORM003: form '{id}' references unknown operation '{operationId}'.");
            if (op.InputType != input)
                throw new InvalidOperationException($"FORM004: form '{id}' input type mismatch with operation '{operationId}'.");
            var build = builder.GetType().GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!;
            formDefs[id] = (FormDefinition)build.Invoke(builder, [id, operationId, null])!;
        }

        var gridDefs = new Dictionary<string, GridDefinition>();
        foreach (var (id, (builder, viewId)) in grids)
        {
            if (!views.TryGetValue(viewId, out var view))
                throw new InvalidOperationException($"GRID001: grid '{id}' references unknown view '{viewId}'.");
            var build = builder.GetType().GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var def = (GridDefinition)build.Invoke(builder, [id, viewId])!;

            foreach (var action in def.RowActions.Concat(def.ToolbarActions))
                if (!operations.ContainsKey(action))
                    throw new InvalidOperationException($"GRID002: grid '{id}' action references unknown operation '{action}'.");

            var resultFields = view.ResultFields.Select(f => f.WireName).ToHashSet();
            foreach (var column in def.Columns)
                if (!resultFields.Contains(column))
                    throw new InvalidOperationException($"VIEW001: grid '{id}' declares column '{column}' not present on view '{viewId}'.");

            gridDefs[id] = def;
        }

        var model = new TamModel
        {
            DefaultCulture = defaultCulture,
            Locales = catalogs,
            Operations = operations,
            Views = views,
            Derivations = derivations,
            Forms = formDefs,
            Grids = gridDefs,
        };

        VerifyLocalization(model, catalogs);
        return model;
    }

    /// <summary>L10N001: every label key the model references must exist in the default culture.</summary>
    private static void VerifyLocalization(TamModel model, LocaleCatalogs catalogs)
    {
        var required = model.Operations.Values.SelectMany(o => o.InputFields.Select(f => f.LabelKey))
            .Concat(model.Operations.Values.Select(o => o.TitleKey))
            .Concat(model.Views.Values.SelectMany(v => v.ResultFields.Select(f => f.LabelKey)));

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
