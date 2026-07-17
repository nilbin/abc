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

    /// <summary>Framework packages (docs/22, the framework-trust tier): registered through the
    /// plugin surface, ALWAYS active for every tenant — activation consumers union these ids in.</summary>
    public IReadOnlyDictionary<string, PackageDefinition> Packages { get; init; } =
        new Dictionary<string, PackageDefinition>();

    /// <summary>Plugin-packaged extension fields on host entities, by entity key (docs/22 P2).</summary>
    public IReadOnlyList<PackagedFieldDefinition> PackagedFields { get; init; } = [];

    /// <summary>Wire keys of entities that accept extensions — the registry validates
    /// tenant/package field definitions against this set (EXT007).</summary>
    public IReadOnlySet<string> ExtensibleEntityKeys { get; init; } = new HashSet<string>();

    /// <summary>The declared navigation trees per surface class (docs/30) — merged from the
    /// host's layout, package/plugin contributions and the mechanical fallback. Empty when the
    /// host declares no nav (the client keeps its own).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<NavNode>> Nav { get; init; } =
        new Dictionary<string, IReadOnlyList<NavNode>>();

    /// <summary>Framework-composed pages (docs/32), by page key — nav { page } targets resolve
    /// a registered custom page first, then one of these.</summary>
    public IReadOnlyDictionary<string, PageDefinition> Pages { get; init; } =
        new Dictionary<string, PageDefinition>();

    /// <summary>Host-declared contribution points (docs/31 D-X4), by slot id.</summary>
    public IReadOnlyDictionary<string, SlotDefinition> Slots { get; init; } =
        new Dictionary<string, SlotDefinition>();

    /// <summary>Non-fatal Build() observations (docs/34 M5): e.g. a convention-derived label
    /// key claimed by members of DIFFERENT aggregates — legal, but one catalog text serves
    /// both, which is how projects once inherited "Order number". Hosts log these at startup.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Every enum the model's fields use, by kebab wire name ("order-type") — the
    /// registry a plugin form references with .EnumOptions() to offer another module's
    /// vocabulary without CLR coupling (docs/34 M6; verified by ENUM001).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Enums { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Plugin panels in host slots, by slot id (docs/31 D-X4).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PanelContribution>> Panels { get; init; } =
        new Dictionary<string, IReadOnlyList<PanelContribution>>();

    /// <summary>Declared domain events (docs/31 D-X5): type + payload fields. OnEffect and
    /// RequiresEvent targets must name one (PLG009) — subscriptions are contracts, not folklore.</summary>
    public IReadOnlyDictionary<string, EventDeclaration> Events { get; init; } =
        new Dictionary<string, EventDeclaration>();

    /// <summary>Plugin row actions on HOST grids, by grid id (docs/31 D-X1).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GridActionContribution>> GridActions { get; init; } =
        new Dictionary<string, IReadOnlyList<GridActionContribution>>();

    /// <summary>Plugins' declared read dependencies on host views (docs/31 D-X3) — build-checked
    /// (PLG008) and the service-mode read whitelist per plugin.</summary>
    public IReadOnlyList<ViewRequirement> ViewRequirements { get; init; } = [];

    /// <summary>Plugin gates by target operation id (docs/22 P2).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GateDefinition>> Gates { get; init; } =
        new Dictionary<string, IReadOnlyList<GateDefinition>>();

    /// <summary>Plugin effect subscribers by event type (docs/22 P2).</summary>
    public IReadOnlyList<SubscriberDefinition> Subscribers { get; init; } = [];

    /// <summary>Plugin-shipped inbound integrations by id (docs/10 + docs/22).</summary>
    public IReadOnlyDictionary<string, PluginIntegrationDefinition> Integrations { get; init; } =
        new Dictionary<string, PluginIntegrationDefinition>();

    /// <summary>Plugin-shipped outbound integrations by id (docs/25).</summary>
    public IReadOnlyDictionary<string, OutboundIntegrationDefinition> OutboundIntegrations { get; init; } =
        new Dictionary<string, OutboundIntegrationDefinition>();

    public IReadOnlyList<string> Permissions =>
        Operations.Values.Select(o => o.Permission)
            .Concat(Views.Values.Select(v => v.Permission))
            // Field-mask atoms (docs/27 D-A3) join the catalogue so roles can grant them.
            .Concat(Operations.Values.SelectMany(o => o.InputFields)
                .Select(f => f.SensitivePermission).OfType<string>())
            .Concat(Views.Values.SelectMany(v => v.ResultFields)
                .Select(f => f.SensitivePermission).OfType<string>())
            // Widening atoms (docs/28 D-AG2, the paired-atom ownership pattern) join the
            // catalogue so roles can grant them and levels can expand into them.
            .Concat(Operations.Values.Select(o => o.DeclaringType)
                .Concat(Views.Values.Select(v => v.DeclaringType))
                .SelectMany(t => t.GetCustomAttributes(typeof(WidensAttribute), inherit: false)
                    .Cast<WidensAttribute>())
                .Select(w => w.Permission))
            .Distinct().Order().ToList();

    public IEnumerable<DerivationDefinition> DerivationsFor(Type inputType) =>
        Derivations.Where(d => d.InputType == inputType);

    /// <summary>Stable key for an extensible entity CLR type: "orders.order" style from namespace-less name.</summary>
    public static string EntityKey(Type entity) => Naming.Kebab(entity.Name);
}
