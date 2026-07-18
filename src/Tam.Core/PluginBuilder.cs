namespace Tam;

/// <summary>A plugin-shipped outbound integration (docs/25).</summary>
public sealed record OutboundIntegrationDefinition(
    string Id, string PluginId, IntegrationTrigger Trigger, OutboundIntegrationHandler Handler)
{
    public string TitleKey => LabelKeys.IntegrationTitle(Id);
}

/// <summary>
/// The plugin's registration surface. <see cref="Model"/> is the host's model builder in
/// plugin scope: everything registered through it is tagged with the plugin id, so the
/// manifest can omit it per tenant and PLG001 can enforce the namespace.
/// </summary>
public sealed class PluginBuilder
{
    internal PluginBuilder(string id, System.Reflection.Assembly assembly, TamModelBuilder model)
    {
        Id = id;
        Assembly = assembly;
        Model = model;
    }

    public string Id { get; }

    /// <summary>The plugin's assembly — the source of embedded locale defaults.</summary>
    public System.Reflection.Assembly Assembly { get; }

    /// <summary>Plugin-scoped model builder — use the assembly's generated AddDiscovered() here.</summary>
    public TamModelBuilder Model { get; }

    /// <summary>
    /// Loads embedded locale defaults by convention: every manifest resource in the plugin
    /// assembly matching <c>*.locales.{culture}.json</c> (ship a <c>locales/</c> folder as
    /// EmbeddedResource). Application locale files override them, like all defaults. Called
    /// automatically by AddPlugin/AddPackage — shipping catalogs is the convention, not a
    /// Configure line.
    /// </summary>
    internal PluginBuilder EmbeddedLocaleDefaults()
    {
        // Memoized per assembly: the eleven framework packages share one assembly and would
        // otherwise re-read + re-deserialize the same embedded catalogs eleven times at startup.
        // Embedded resources are immutable, so a process-lifetime cache is safe.
        var catalogs = embeddedLocales.GetOrAdd(Assembly, static assembly =>
        {
            var loaded = new List<(string Culture, IReadOnlyDictionary<string, string> Entries)>();
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var parts = resource.Split('.');
                // "{Root}.locales.{culture}.json" — culture is the second-to-last segment.
                if (parts.Length < 4 || parts[^1] != "json" || parts[^3] != "locales") continue;
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) continue;
                loaded.Add((parts[^2], System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(stream) ?? []));
            }
            return loaded;
        });
        foreach (var (culture, entries) in catalogs) Model.LocaleDefaults(culture, entries);
        return this;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        System.Reflection.Assembly,
        IReadOnlyList<(string Culture, IReadOnlyDictionary<string, string> Entries)>>
        embeddedLocales = new();

    /// <summary>Plugin locale defaults (programmatic form); application locale files override them.</summary>
    public PluginBuilder LocaleDefaults(string culture, IReadOnlyDictionary<string, string> entries)
    {
        Model.LocaleDefaults(culture, entries);
        return this;
    }

    /// <summary>
    /// Packages an extension field onto a host entity, addressed by entity key (the wire name —
    /// a plugin never references host CLR types). The key is prefixed with the plugin id, so
    /// collisions with tenant fields and other plugins are impossible. Labels come from the
    /// plugin's locale defaults under "ext.{pluginId}.{key}" — display text lives in catalogs,
    /// never in code (D6), for plugins too.
    /// </summary>
    public PluginBuilder ExtensionField(
        string entityKey, string key, string type,
        bool required = false, int? maxLength = null, IReadOnlyList<string>? options = null,
        bool readOnly = false)
    {
        Model.PackagedField(entityKey, $"{Id}.{key}", type, required, maxLength, options, readOnly);
        return this;
    }

    /// <summary>
    /// Contributes navigation CONTENT (docs/30 D-N2): pages plus a suggested semantic section
    /// slug ("administration", "work"). Placement is the host's, then the tenant's — a plugin
    /// never names a mode, slot or position in a layout it cannot see.
    /// </summary>
    public PluginBuilder Nav(Action<NavContributionBuilder> configure)
    {
        var builder = new NavContributionBuilder(Id);
        configure(builder);
        foreach (var contribution in builder.Contributions)
            Model.NavContribute(contribution);
        return this;
    }

    /// <summary>
    /// Attaches one of THIS plugin's operations as a row action on a HOST grid (docs/31 D-X1),
    /// with a declared input↔column bind. Placement mirrors nav: the plugin suggests, tenant
    /// activation and user permission decide visibility. Validated at Build (PLG006).
    /// </summary>
    public PluginBuilder GridAction(string gridId, string operationId,
        Action<GridActionBindBuilder> bind)
    {
        var builder = new GridActionBindBuilder();
        bind(builder);
        Model.GridAction(gridId, operationId, builder.Binds);
        return this;
    }

    /// <summary>
    /// Declares a read dependency on a host VIEW (docs/31 D-X3): the view and the result fields
    /// this plugin reads, verified to exist at Build (PLG008) — compatibility is a compile-time
    /// fact and a capability-manifest line. Also the whitelist for service-mode reads
    /// (<c>IHostViewReader</c>) in effect handlers, where no actor exists.
    /// </summary>
    public PluginBuilder RequiresView(string viewId, params string[] fields)
    {
        Model.RequireView(viewId, fields.Select(BareName).ToArray());
        return this;
    }

    // A declared field may carry a ":kind" suffix ("estimatedTotal:decimal") — consumed ONLY
    // by the source generator's typed facades (docs/31); the wire contract, PLG008/PLG009 and
    // the service-mode whitelist see the bare name.
    private static string BareName(string field) => Naming.Camel(field.Split(':')[0]);

    /// <summary>
    /// Contributes a panel into a HOST-declared slot (docs/31 D-X4): this plugin's own grid,
    /// its query fields bound to the slot's record-context keys. The host opts a surface in
    /// once; panels land there without the host naming any plugin. Validated at Build (PLG007).
    /// </summary>
    public PluginBuilder Panel(string slotId, string grid, Action<PanelBindBuilder> bind,
        string? heading = null, int? order = null)
    {
        var builder = new PanelBindBuilder();
        bind(builder);
        Model.Panel(slotId, grid, heading, order, builder.Binds);
        return this;
    }

    /// <summary>Declares a dependency on an EVENT contract (docs/31 D-X5): PLG009 verifies the
    /// event is declared and carries the named payload fields — the payload shape this plugin's
    /// subscribers read stops being folklore.</summary>
    public PluginBuilder RequiresEvent(string eventType, params string[] fields)
    {
        Model.RequireEvent(eventType, fields);   // "name[:kind]" — parsed + kind-checked (PLG009)
        return this;
    }

    /// <summary>Composes a registration PART (review round 4): big plugins split Configure
    /// into cohesive units and list them here — explicitly, so Configure stays the index.</summary>
    public PluginBuilder AddPart<TPart>() where TPart : IPluginPart, new()
    {
        new TPart().Configure(this);
        return this;
    }

    /// <summary>Binds a form (docs/32 defaults apply) — the single-receiver twin of
    /// TamModelBuilder.Form, so plugin code never flips between plugin.* and plugin.Model.*.</summary>
    public PluginBuilder Form<TInput>(string id, string operationId,
        Action<FormBuilder<TInput>>? configure = null)
    {
        Model.Form(id, operationId, configure);
        return this;
    }

    /// <summary>Binds a grid (docs/32 defaults apply) — single-receiver twin.</summary>
    public PluginBuilder Grid<TResult>(string id, string viewId,
        Action<GridBuilder<TResult>>? configure = null)
    {
        Model.Grid(id, viewId, configure);
        return this;
    }

    /// <summary>Declares a framework-composed page for the plugin's OWN aggregate (docs/32,
    /// review round 4): the id sits under the plugin prefix (PLG001), the manifest filters it
    /// by activation, and placement stays the host's/tenant's via the nav suggestion.</summary>
    public PluginBuilder Page(string id, Action<PageBuilder> configure)
    {
        Model.Page(id, configure);
        return this;
    }

    /// <summary>Declares a domain event contract (docs/31 D-X5) under the plugin prefix.</summary>
    public PluginBuilder PublishesEvent(string eventType, params string[] fields)
    {
        Model.PublishesEvent(eventType, fields);
        return this;
    }

    /// <summary>Explicit add-by-type twins for the shared-assembly package tier (which cannot
    /// use per-assembly AddDiscovered): operations, views, gates, subscribers.</summary>
    public PluginBuilder AddOperationType(Type type) { Model.AddOperationType(type); return this; }

    public PluginBuilder AddViewType(Type type) { Model.AddViewType(type); return this; }

    public PluginBuilder AddDerivationHost(Type type) { Model.AddDerivationHost(type); return this; }

    public PluginBuilder AddGateType(Type type) { Model.AddGateType(type); return this; }

    public PluginBuilder AddSubscriberType(Type type) { Model.AddSubscriberType(type); return this; }

    public PluginBuilder Gate<TGate>(string operationId, bool pure = false)
        where TGate : class, IOperationGate
    {
        Model.Gate(operationId, typeof(TGate), pure);
        return this;
    }

    /// <summary>Declares a WILDCARD gate (docs/28 approvals seam 1): runs on EVERY operation,
    /// after any operation-specific gates, and decides from its own data whether to act — the
    /// gated set is tenant config, not compile time. A PURE wildcard gate (pure-over-input,
    /// no state reads it needs transactional protection for) runs BEFORE the transaction.</summary>
    public PluginBuilder GateAll<TGate>(bool pure = false) where TGate : class, IOperationGate
    {
        Model.Gate(GateDefinition.Wildcard, typeof(TGate), pure);
        return this;
    }

    /// <summary>Registers a reach kind (docs/35) under this plugin's prefix — the plugin's
    /// people-sets (groups, teams) become REFERENCABLE by host domains (a folder ACL naming
    /// <c>approvals.group:…</c>) without the host learning group semantics. Kinds from an
    /// inactive plugin resolve to "not within" — activation gates resolution, never storage.</summary>
    public PluginBuilder ReachProvider<TProvider>(string kind)
        where TProvider : class, IReachProvider
    {
        Model.ReachProvider<TProvider>(kind);
        return this;
    }

    /// <summary>Subscribes to committed event effects (the outbox). The handler is constructed
    /// per delivery with ctor injection, post-commit, in a scope pinned to the record's
    /// tenant; runs only for tenants with this plugin active.</summary>
    public PluginBuilder OnEffect<THandler>(string eventType) where THandler : class, IEffectHandler
    {
        Model.OnEffect(eventType, typeof(THandler));
        return this;
    }

    /// <summary>
    /// Ships an inbound integration (docs/10) targeting a host operation. Mapped to
    /// <c>POST /api/integrations/{Id}.{id}</c>, activation-gated, inbox-idempotent. The id is
    /// namespaced under the plugin (PLG001); the target operation is a host wire id.
    /// </summary>
    public PluginBuilder Integration(
        string id, string operationId, IntegrationKeySelector key, IntegrationRowMapper map)
    {
        Model.Integration($"{Id}.{id}", operationId, key, map);
        return this;
    }

    /// <summary>
    /// Ships an outbound integration (docs/25) that fetches from or pushes to an external system.
    /// Triggered by a committed event, a tenant-configured schedule, or a manual call; reads
    /// settings and secrets from the vault; activation-gated. The id is plugin-namespaced.
    /// </summary>
    public PluginBuilder OutboundIntegration(
        string id, IntegrationTrigger trigger, OutboundIntegrationHandler handler)
    {
        Model.OutboundIntegration($"{Id}.{id}", trigger, handler);
        return this;
    }
}
