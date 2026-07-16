namespace Tam;

// The plugin HANDLER seams (docs/22 P2 + docs/28): the class-based contracts a plugin
// implements — gates, effect handlers, parked work — plus the definitions the compiled model
// carries for them, and the integration seam delegates (docs/10 + docs/25).

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

/// <summary>A plugin's row action on a HOST grid (docs/31 D-X1): the plugin's own operation,
/// with a DECLARED input↔column bind — wire names both sides, validated at Build (PLG006).</summary>
public sealed record GridActionContribution(
    string GridId, string OperationId, string PluginId,
    IReadOnlyList<(string Input, string Column)> Bind);

/// <summary>A plugin's declared read dependency on a host VIEW (docs/31 D-X3): compatibility
/// as a build-time fact (PLG008) and the service-mode read whitelist for effect handlers.</summary>
public sealed record ViewRequirement(string ViewId, string PluginId, IReadOnlyList<string> Fields);

/// <summary>A declared domain event (docs/31 D-X5): the payload contract subscribers and
/// event-triggered integrations bind to. Owner is the declaring plugin, or null for host events.</summary>
public sealed record EventDeclaration(string EventType, IReadOnlyList<string> Fields, string? Plugin);

/// <summary>A plugin's declared dependency on an EVENT contract (docs/31 D-X5): PLG009 verifies
/// the event is declared and carries the named payload fields.</summary>
public sealed record EventRequirement(string EventType, string PluginId, IReadOnlyList<string> Fields);

/// <summary>Declared-bind author for <see cref="PluginBuilder.GridAction"/>.</summary>
public sealed class GridActionBindBuilder
{
    internal List<(string Input, string Column)> Binds { get; } = [];

    public GridActionBindBuilder Field(string input, string fromColumn)
    {
        Binds.Add((Naming.Camel(input), Naming.Camel(fromColumn)));
        return this;
    }
}

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

/// <summary>
/// Declares which operation this <see cref="IOperationGate"/> class gates — registration lives
/// ON the behavior, mirroring [Operation]/[View]: the assembly's generated AddDiscovered()
/// registers it, so a big plugin's Configure stays a table of contents (docs/22, review round
/// 4). The fluent twin (PluginBuilder.Gate&lt;T&gt;) remains the substrate, like AddOperationType.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GateAttribute(string operationId) : Attribute
{
    public string OperationId { get; } = operationId;

    /// <summary>PURE gates run outside the operation transaction (docs/28).</summary>
    public bool Pure { get; init; }
}

/// <summary>The wildcard form of <see cref="GateAttribute"/>: gates EVERY operation.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GateAllAttribute : Attribute
{
    public bool Pure { get; init; }
}

/// <summary>
/// Declares which committed event this <see cref="IEffectHandler"/> class subscribes to
/// (at-least-once, via the outbox). Multiple attributes subscribe the class to multiple events.
/// PLG009 still verifies every target against a declared event contract at Build().
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class OnEffectAttribute(string eventType) : Attribute
{
    public string EventType { get; } = eventType;
}
