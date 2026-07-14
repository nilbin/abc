namespace Tam;

/// <summary>
/// Names a plugin assembly's permanent namespace prefix (docs/22, decision D8). Every id the
/// plugin contributes — operations, views, forms, grids, permissions — must start with
/// "{id}." (PLG001). Wire-name permanence (D4) applies to the prefix itself.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TamPluginAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>
/// A compiled, curated extensibility module: reviewed code bound at host build time, activated
/// per tenant as runtime data. Tenants never upload code — that line is D8's core commitment.
/// </summary>
public interface ITamPlugin
{
    void Configure(PluginBuilder plugin);
}

/// <summary>A registered plugin in the compiled model. Activation state is tenant data, not model.</summary>
public sealed record PluginDefinition(string Id)
{
    public string TitleKey => $"plugins.{Id}.title";
}

/// <summary>
/// A plugin-packaged extension field on a HOST entity (docs/22 P2): same spec, same
/// ExtensionData storage, same wire channel as tenant custom fields — compiled origin,
/// key-prefixed with the plugin id, present only for tenants with the plugin active.
/// </summary>
public sealed record PackagedFieldDefinition(string PluginId, string EntityKey, ExtensionFieldSpec Spec);

/// <summary>What a gate sees: the raw wire input (a plugin couples to the host's wire
/// contract, never its CLR types), the execution context, and scoped services.</summary>
public sealed record GateContext(
    System.Text.Json.JsonElement Input,
    OperationContext Context,
    IServiceProvider Services);

public delegate Task<Result> OperationGate(GateContext gate, CancellationToken ct);

/// <summary>A typed precondition a plugin declares on a host operation (docs/22 P2): runs
/// after authorization and structural validation, before the handler. Visible in the manifest
/// as GatedBy — the coupling is declared, never magic.</summary>
public sealed record GateDefinition(string OperationId, string PluginId, OperationGate Handler);

public sealed record EffectEvent(
    string TenantId,
    string OperationId,
    string EventType,
    System.Text.Json.JsonElement Payload);

public delegate Task EffectSubscriber(EffectEvent effect, IServiceProvider services, CancellationToken ct);

/// <summary>A plugin's reaction to a committed host effect (docs/22 P2): delivered from the
/// outbox after the operation's transaction, never by patching host handlers.</summary>
public sealed record SubscriberDefinition(string EventType, string PluginId, EffectSubscriber Handler);

/// <summary>
/// The plugin's registration surface. <see cref="Model"/> is the host's model builder in
/// plugin scope: everything registered through it is tagged with the plugin id, so the
/// manifest can omit it per tenant and PLG001 can enforce the namespace.
/// </summary>
public sealed class PluginBuilder
{
    internal PluginBuilder(string id, TamModelBuilder model)
    {
        Id = id;
        Model = model;
    }

    public string Id { get; }

    /// <summary>Plugin-scoped model builder — use the assembly's generated AddDiscovered() here.</summary>
    public TamModelBuilder Model { get; }

    /// <summary>Plugin locale defaults (embedded resources); application locale files override them.</summary>
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
        bool required = false, int? maxLength = null, IReadOnlyList<string>? options = null)
    {
        Model.PackagedField(entityKey, $"{Id}.{key}", type, required, maxLength, options);
        return this;
    }

    /// <summary>Declares a precondition on a host operation (by operation id — the wire
    /// contract). Runs only for tenants with this plugin active; listed in the manifest.</summary>
    public PluginBuilder Gate(string operationId, OperationGate gate)
    {
        Model.Gate(operationId, gate);
        return this;
    }

    /// <summary>Subscribes to committed event effects (the outbox). Runs post-commit, per
    /// tenant-with-plugin-active, in its own service scope.</summary>
    public PluginBuilder OnEffect(string eventType, EffectSubscriber handler)
    {
        Model.OnEffect(eventType, handler);
        return this;
    }
}
