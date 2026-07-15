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

/// <summary>What a gate sees: the operation being guarded (so a WILDCARD gate can decide from its
/// own config which operations it applies to — docs/28 approvals seam 1), the raw wire input (a
/// plugin couples to the host's wire contract, never its CLR types), the pipeline's SHA-256 hash
/// of that input (the idempotency hash — the natural dedupe/integrity key for a parked envelope),
/// the execution context, and scoped services.</summary>
public sealed record GateContext(
    string OperationId,
    System.Text.Json.JsonElement Input,
    string PayloadHash,
    OperationContext Context,
    IServiceProvider Services)
{
    private List<Func<IServiceProvider, CancellationToken, Task>>? parked;

    /// <summary>
    /// Defers work to run only if THIS gate's result blocks the operation — after the domain
    /// transaction has rolled back, in a FRESH service scope pinned to the same tenant (docs/28
    /// approvals seam 2). The one sanctioned way for a gate to keep state from a blocked attempt:
    /// writes inside the gate body itself die with the rollback, and a gate that wrote before
    /// deciding could leak state from attempts it allowed. Park the envelope here; if the gate
    /// returns success the parked work is discarded.
    /// </summary>
    public void Park(Func<IServiceProvider, CancellationToken, Task> work) =>
        (parked ??= []).Add(work);

    /// <summary>The deferred work, drained by the pipeline when the gate blocks. Empty otherwise.</summary>
    public IReadOnlyList<Func<IServiceProvider, CancellationToken, Task>> ParkedWork =>
        parked ?? (IReadOnlyList<Func<IServiceProvider, CancellationToken, Task>>)[];
}

public delegate Task<Result> OperationGate(GateContext gate, CancellationToken ct);

/// <summary>A typed precondition a plugin declares (docs/22 P2): runs after authorization and
/// structural validation, before the handler. An operation-specific gate names one operation id;
/// a WILDCARD gate (<see cref="OperationId"/> = "*") runs on EVERY operation and decides from its
/// own config whether to act — the approvals seam (docs/28 tutorial Step 16), where the set of
/// gated operations is tenant data, not compile-time. Visible in the manifest as GatedBy.</summary>
public sealed record GateDefinition(string OperationId, string PluginId, OperationGate Handler)
{
    public const string Wildcard = "*";
}

public sealed record EffectEvent(
    string TenantId,
    string OperationId,
    string EventType,
    System.Text.Json.JsonElement Payload);

public delegate Task EffectSubscriber(EffectEvent effect, IServiceProvider services, CancellationToken ct);

/// <summary>A plugin's reaction to a committed host effect (docs/22 P2): delivered from the
/// outbox after the operation's transaction, never by patching host handlers.</summary>
public sealed record SubscriberDefinition(string EventType, string PluginId, EffectSubscriber Handler);

/// <summary>The stable idempotency key of one source row (e.g. the vendor's document number).
/// Cheap and pure over the source — computed at receive time to dedupe.</summary>
public delegate string IntegrationKeySelector(System.Text.Json.JsonElement sourceRow);

/// <summary>
/// Maps one external source row to the target operation's wire input (member wire names →
/// values). Re-run on every retry with fresh services, so external-identity resolution against
/// host VIEWS (the actor's permissions) is re-evaluated — a row that failed because a customer
/// didn't exist yet recovers once it does, with no re-send. No host CLR types.
/// </summary>
public delegate Task<IReadOnlyDictionary<string, object?>> IntegrationRowMapper(
    System.Text.Json.JsonElement sourceRow, IServiceProvider services, OperationContext context, CancellationToken ct);

/// <summary>
/// A plugin-shipped inbound integration (docs/10 + docs/22): an external system posts a JSON
/// array to <c>/api/integrations/{id}</c>; each element is stored in the inbox under its key,
/// then mapped and run through the target host operation with idempotency + retry + dead-letter
/// — activation-gated like every plugin surface. The SOURCE is stored and the mapper re-runs on
/// retry, so a fixed root cause (a customer created later) recovers automatically.
/// </summary>
public sealed record PluginIntegrationDefinition(
    string Id, string PluginId, string OperationId,
    IntegrationKeySelector Key, IntegrationRowMapper Map);

// ---- Outbound integrations (docs/25): fetch/push to external systems, triggered by event,
//      schedule or manual call. The handler reads settings and secrets from the vault and does
//      the HTTP; a run is recorded either way. All activation-gated.

/// <summary>How an outbound integration fires.</summary>
public abstract record IntegrationTrigger;

/// <summary>Fire when a host effect of this event type commits (via the outbox).</summary>
public sealed record EventTrigger(string EventType) : IntegrationTrigger;

/// <summary>Fire on a schedule the tenant configures (<c>every:15m</c>, <c>daily:02:00</c>).</summary>
public sealed record ScheduleTrigger : IntegrationTrigger;

/// <summary>Fire only when a tenant admin calls it (integrations.run).</summary>
public sealed record ManualTrigger : IntegrationTrigger;

/// <summary>What an outbound handler is given: the vault (settings + secrets), an HttpClient,
/// the pipeline (to write results back through operations), the triggering event payload if
/// any, and the execution context. No host CLR types leak in.</summary>
public interface IIntegrationRunContext
{
    OperationContext Context { get; }
    IServiceProvider Services { get; }
    System.Net.Http.HttpClient Http { get; }

    /// <summary>Non-secret tenant config (base URLs, ids). Null if unset.</summary>
    Task<string?> Setting(string key, CancellationToken ct);

    /// <summary>A decrypted tenant secret (API key, token). Null if unset. Never logged.</summary>
    Task<string?> Secret(string key, CancellationToken ct);

    /// <summary>The committed effect payload that triggered this run (event trigger only).</summary>
    System.Text.Json.JsonElement? EventPayload { get; }
}

/// <summary>Result of one outbound run: success + a short human detail for the run log.</summary>
public sealed record OutboundResult(bool Ok, string? Detail = null)
{
    public static OutboundResult Success(string? detail = null) => new(true, detail);
    public static OutboundResult Failure(string detail) => new(false, detail);
}

public delegate Task<OutboundResult> OutboundIntegrationHandler(
    IIntegrationRunContext run, CancellationToken ct);

/// <summary>A plugin-shipped outbound integration (docs/25).</summary>
public sealed record OutboundIntegrationDefinition(
    string Id, string PluginId, IntegrationTrigger Trigger, OutboundIntegrationHandler Handler)
{
    public string TitleKey => $"integrations.{Id}.title";
}

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
