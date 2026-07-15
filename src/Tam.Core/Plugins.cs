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
/// Names a FRAMEWORK PACKAGE: the framework-trust tier of the plugin system (docs/22). A package
/// registers through the same <see cref="PluginBuilder"/> surface a vendor plugin uses — the
/// framework's own admin capabilities dogfood the seams they sell — but differs on the tier axes:
/// always active for every tenant (never in the activation table, never entitlement-gated), and
/// it CLAIMS existing wire prefixes ("users", "audit") instead of being namespaced under its id —
/// those wire names are live and permanent (D4). Prefix claims are validated like PLG001.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TamPackageAttribute(string id, params string[] prefixes) : Attribute
{
    public string Id { get; } = id;

    /// <summary>The wire prefixes this package owns; every id/permission it registers must sit
    /// under one of them ("users" covers "users.invite" and permission "users.manage").</summary>
    public string[] Prefixes { get; } = prefixes;
}

/// <summary>A framework package in the compiled model. Always active — there is no row.</summary>
public sealed record PackageDefinition(string Id, IReadOnlyList<string> Prefixes);

/// <summary>
/// A plugin-packaged extension field on a HOST entity (docs/22 P2): same spec, same
/// ExtensionData storage, same wire channel as tenant custom fields — compiled origin,
/// key-prefixed with the plugin id, present only for tenants with the plugin active.
/// </summary>
public sealed record PackagedFieldDefinition(string PluginId, string EntityKey, ExtensionFieldSpec Spec);

/// <summary>
/// Constructs plugin handler classes (gates, effect handlers, parked work) with constructor
/// injection from the CURRENT scope. Defined in core so seam types can name it without core
/// referencing a DI package; implemented over ActivatorUtilities in the host layer. Handlers are
/// ordinary classes — no DI registration required, dependencies are ctor parameters.
/// </summary>
public interface ITamActivator
{
    object Create(Type handlerType);
}

/// <summary>
/// A typed precondition on a host operation (docs/22 P2): constructed per invocation with
/// constructor injection (declare <c>ITamDb</c>, <c>TamModel</c>, … as ctor parameters — never a
/// service locator), it runs after authorization and structural validation, before the handler,
/// INSIDE the transaction. Blocks by returning an error result.
/// </summary>
public interface IOperationGate
{
    Task<Result> CheckAsync(GateContext gate, CancellationToken ct);
}

/// <summary>
/// A plugin's reaction to a committed host effect (docs/22 P2): constructed per delivery with
/// constructor injection, in a scope pinned to the record's tenant. Delivery is at-least-once —
/// handlers stay idempotent.
/// </summary>
public interface IEffectHandler
{
    Task HandleAsync(EffectEvent effect, CancellationToken ct);
}

/// <summary>
/// Work a blocking gate parks across the rollback (docs/28 approvals seam 2). Constructed by the
/// pipeline in a FRESH tenant-pinned scope AFTER the domain transaction rolled back — so an
/// injected <c>ITamDb</c> is a fresh context by construction; the rolled-back gate scope is
/// unreachable. State crosses from the gate as the explicit <typeparamref name="TState"/> value.
/// </summary>
public interface IParkedWork<in TState>
{
    Task RunAsync(TState state, CancellationToken ct);
}

/// <summary>What a gate sees: the operation being guarded (so a WILDCARD gate can decide from its
/// own config which operations it applies to — docs/28 approvals seam 1), the raw wire input (a
/// plugin couples to the host's wire contract, never its CLR types), the pipeline's SHA-256 hash
/// of that input (the idempotency hash — the natural dedupe/integrity key for a parked envelope),
/// and the execution context. Dependencies belong on the gate class's CONSTRUCTOR, not here.</summary>
public sealed record GateContext(
    string OperationId,
    System.Text.Json.JsonElement Input,
    string PayloadHash,
    OperationContext Context)
{
    private List<Func<IServiceProvider, CancellationToken, Task>>? parked;

    /// <summary>
    /// Defers work to run only if THIS gate's result blocks the operation — after the domain
    /// transaction has rolled back, in a FRESH service scope pinned to the same tenant (docs/28
    /// approvals seam 2). The one sanctioned way for a gate to keep state from a blocked attempt:
    /// writes inside the gate body itself die with the rollback, and a gate that wrote before
    /// deciding could leak state from attempts it allowed. <typeparamref name="TWork"/> is
    /// constructed IN the fresh scope, so its injected services cannot be the rolled-back ones;
    /// if the gate returns success the parked work is discarded.
    /// </summary>
    public void Park<TWork, TState>(TState state) where TWork : class, IParkedWork<TState> =>
        (parked ??= []).Add((services, ct) =>
        {
            var activator = services.GetService(typeof(ITamActivator)) as ITamActivator
                ?? throw new InvalidOperationException("DI001: ITamActivator is not registered.");
            return ((IParkedWork<TState>)activator.Create(typeof(TWork))).RunAsync(state, ct);
        });

    /// <summary>The deferred work, drained by the pipeline when the gate blocks. Empty otherwise.</summary>
    public IReadOnlyList<Func<IServiceProvider, CancellationToken, Task>> ParkedWork =>
        parked ?? (IReadOnlyList<Func<IServiceProvider, CancellationToken, Task>>)[];
}

/// <summary>A declared gate (docs/22 P2): the handler TYPE (resolved per invocation via
/// <see cref="ITamActivator"/>) bound to one operation id — or to the WILDCARD
/// (<see cref="OperationId"/> = "*"), which runs on EVERY operation and decides from its own
/// config whether to act (docs/28 tutorial Step 16: the set of gated operations is tenant data,
/// not compile-time). A PURE gate declares itself pure-over-input and runs BEFORE the
/// transaction — the cheap fail (tenant automation rules ride here); transactional gates run
/// inside it, where the state they check cannot change underneath the handler. Visible in the
/// manifest as GatedBy.</summary>
public sealed record GateDefinition(string OperationId, string PluginId, Type HandlerType, bool Pure = false)
{
    public const string Wildcard = "*";
}

public sealed record EffectEvent(
    string TenantId,
    string OperationId,
    string EventType,
    System.Text.Json.JsonElement Payload);

/// <summary>A declared effect subscription (docs/22 P2): the handler TYPE, delivered from the
/// outbox after the operation's transaction, never by patching host handlers.</summary>
public sealed record SubscriberDefinition(string EventType, string PluginId, Type HandlerType);

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
    /// EmbeddedResource). Application locale files override them, like all defaults.
    /// </summary>
    public PluginBuilder LocaleDefaults()
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
        bool required = false, int? maxLength = null, IReadOnlyList<string>? options = null)
    {
        Model.PackagedField(entityKey, $"{Id}.{key}", type, required, maxLength, options);
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

    /// <summary>Declares a precondition on a host operation (by operation id — the wire
    /// contract). <typeparamref name="TGate"/> is constructed per invocation with ctor injection.
    /// Runs only for tenants with this plugin active; listed in the manifest.</summary>
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
