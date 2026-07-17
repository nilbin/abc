using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

// The tenant-rules ENGINE (docs/22 P5) — core infrastructure, like Outbox and the executors.
// Packages/Rules.cs is the tam.rules SURFACE over it (operations, views, form, grid), following
// the tier's split rule: the service is core; the package is only the surface. The engine runs
// in three seams — the pure gate phase, the transactional gate phase, and the outbox dispatch
// path — through ONE evaluation core below, so fail-open/fail-closed semantics cannot drift.

/// <summary>
/// Tenant automation rules as the tam.rules package's own PURE wildcard gate — the framework's
/// P5 feature runs through the very gate seam it sells (docs/22): no hard call in the executor,
/// no special case. Pure-over-input, so it rides the pre-transaction phase (the cheap fail);
/// which operations it fires on is AutomationRuleEntity data, exactly like the approvals
/// plugin's rule table.
/// </summary>
internal sealed class RulesGate(ITamDb tam, TamModel model) : IOperationGate
{
    public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
    {
        var findings = await RuleEvaluator.EvaluateAsync(
            tam.Db, gate.OperationId, gate.Input, gate.Context, model.DefaultCulture, ct);
        return findings.Count == 0 ? Result.Success() : new Result { Findings = findings };
    }
}

/// <summary>docs/22 action catalog: the transactional twin of <see cref="RulesGate"/> —
/// evaluates ACTION rules and performs their writes inside the operation's transaction
/// (set-field rides the tracked row into the operation's own SaveChanges; publish-event is
/// an outbox row in the same commit). Never blocks; only unevaluable rules warn.</summary>
internal sealed class RuleActionsGate(ITamDb tam, IExtensionRegistry registry) : IOperationGate
{
    public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
    {
        var findings = await RuleEvaluator.ExecuteActionsAsync(
            tam.Db, gate.OperationId, gate.Input, gate.Context, registry, ct);
        return new Result { Findings = findings };
    }
}

/// <summary>
/// Tenant automation rules (docs/22 P5): declarative validation authored as data. The
/// condition is the same portable Px AST forms already evaluate — structured, analyzable,
/// bounded — never a string-parsed expression. The action set is closed: the blocking
/// finding (the Salesforce validation rule), set-field, and publish-event.
/// </summary>
public static class RuleEvaluator
{
    internal sealed record RuleAction(string Type, string? Field = null, JsonElement? Value = null);

    /// <summary>One rule's condition, evaluated ONCE for all three seams: deserialize the Px,
    /// hydrate the target row (tracked for the transactional paths, detached-and-memoized for
    /// the pure pass), fire over wire-identical values. A MISSING row means the rule does not
    /// fire (the pipeline's own not-found follows); only a rule that cannot EVALUATE throws —
    /// the callers' shared catch decides warn-vs-skip per seam.</summary>
    private sealed record Firing(bool Fired, object? Row, bool RowMissing);

    private static async Task<Firing> EvaluateRuleAsync(
        DbContext db, AutomationRuleEntity rule, JsonElement body, string tenantId,
        bool trackRow, Dictionary<string, JsonElement?>? rowCache, CancellationToken ct)
    {
        var condition = JsonSerializer.Deserialize<Px>(rule.ConditionJson, TamJson.Options)!;
        object? row = null;
        JsonElement? rowJson = null;
        if (rule is { RowEntityKey: { } entityKey, RowIdField: { } idField })
        {
            var cacheKey = $"{entityKey}|{FieldValue(body, idField)}";
            if (!trackRow && rowCache is not null && rowCache.TryGetValue(cacheKey, out rowJson))
            {
                // memoized pure-pass read: one load per (entity, id) across the rule set
            }
            else
            {
                (row, rowJson) = await RowAsync(db, entityKey, FieldValue(body, idField), tenantId, ct);
                if (!trackRow)
                {
                    // Pure phase: DETACH after reading so the pre-transaction load never lingers
                    // in the identity map — the handler and the transactional gate re-read fresh
                    // inside the transaction rather than seeing this stale snapshot (round 5, F3).
                    if (row is not null) db.Entry(row).State = EntityState.Detached;
                    row = null;
                    if (rowCache is not null) rowCache[cacheKey] = rowJson;
                }
            }
            if (rowJson is null) return new(false, null, RowMissing: true);
        }
        var fired = PxBinary.Truthy(condition.Evaluate(name =>
            name.StartsWith("row.", StringComparison.Ordinal)
                ? rowJson is { } r ? FieldValue(r, name["row.".Length..]) : null
                : FieldValue(body, name)));
        return new(fired, row, RowMissing: false);
    }

    /// <summary>The closed set of "this rule cannot evaluate/apply" faults — never brick the
    /// operation (fail-open would skip validation silently; fail-closed would block the
    /// tenant). One list for all three seams.</summary>
    private static bool IsRuleFault(Exception e) => e is JsonException or NotSupportedException
        or ArgumentException or FormatException or InvalidOperationException;

    /// <summary>
    /// Evaluates the tenant's active FINDING rules for this operation against the wire input
    /// (the pure gate phase). A firing rule contributes a blocking finding whose message is
    /// the rule's own tenant-authored text in the request culture ("rules.{name}", D6 as data).
    /// </summary>
    public static async Task<List<Finding>> EvaluateAsync(
        DbContext db, string operationId, JsonElement body, OperationContext context,
        string defaultCulture, CancellationToken ct)
    {
        var rules = await db.Set<AutomationRuleEntity>()
            .Where(x => x.OnOperation == operationId && !x.Retired && x.ActionJson == null)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        if (rules.Count == 0) return [];

        var findings = new List<Finding>();
        var rowCache = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            Firing firing;
            try
            {
                firing = await EvaluateRuleAsync(
                    db, rule, body, context.TenantId.Value, trackRow: false, rowCache, ct);
            }
            catch (Exception e) when (IsRuleFault(e))
            {
                findings.Add(Finding.Warning("rules.evaluation-failed").With(("rule", rule.Name)));
                continue;
            }
            if (firing.RowMissing || !firing.Fired) continue;

            var messages = JsonSerializer.Deserialize<Dictionary<string, string>>(rule.MessagesJson) ?? [];
            findings.Add(Finding.Error($"rules.{rule.Name}").Create() with
            {
                // culture -> default culture -> anything: RUL003 guarantees the middle one exists.
                Message = messages.GetValueOrDefault(context.Culture)
                    ?? messages.GetValueOrDefault(defaultCulture)
                    ?? messages.Values.FirstOrDefault(),
                Targets = rule.TargetField is { Length: > 0 } target
                    ? [target.StartsWith("ext.", StringComparison.Ordinal)
                        ? FieldPath.Extension(target["ext.".Length..])
                        : FieldPath.For(target)]
                    : [],
            });
        }
        return findings;
    }

    /// <summary>The action-rule pass (docs/22 action catalog), run in the TRANSACTIONAL gate
    /// phase: a firing set-field mutates the target row's extensions on the tracked context
    /// (committed by the operation's own SaveChanges, audited on the operation's entry);
    /// a firing publish-event adds an outbox row with the derived type "rules.{name}" and
    /// the operation's wire input as payload — same commit, dispatched like any event.</summary>
    internal static async Task<List<Finding>> ExecuteActionsAsync(
        DbContext db, string operationId, JsonElement body, OperationContext context,
        IExtensionRegistry registry, CancellationToken ct)
    {
        // Deterministic order (review round 5, F4): two action rules that touch the same field
        // must resolve the same way on every provider and run.
        var rules = await db.Set<AutomationRuleEntity>()
            .Where(x => x.OnOperation == operationId && !x.Retired && x.ActionJson != null)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        if (rules.Count == 0) return [];

        var findings = new List<Finding>();
        var specCache = new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            try
            {
                var firing = await EvaluateRuleAsync(
                    db, rule, body, context.TenantId.Value, trackRow: true, rowCache: null, ct);
                if (firing.RowMissing || !firing.Fired) continue;

                var action = JsonSerializer.Deserialize<RuleAction>(rule.ActionJson!, TamJson.Options)!;
                switch (action.Type)
                {
                    case "set-field":
                        if (!await TryApplySetFieldAsync(
                                specCache, registry, context.TenantId, rule, action, firing.Row, ct))
                            findings.Add(Finding.Warning("rules.evaluation-failed").With(("rule", rule.Name)));
                        break;
                    case "publish-event":
                        db.Add(new OutboxRecord
                        {
                            Id = Guid.NewGuid(),
                            TenantId = context.TenantId.Value,
                            OperationId = operationId,
                            EventType = $"rules.{rule.Name}",
                            PayloadJson = body.GetRawText(),
                            CreatedAtIso = IsoTime.Now(),
                        });
                        break;
                }
            }
            catch (Exception e) when (IsRuleFault(e))
            {
                findings.Add(Finding.Warning("rules.evaluation-failed").With(("rule", rule.Name)));
            }
        }
        return findings;
    }

    /// <summary>
    /// The effect-triggered-rules pass (docs/22): tenant rules whose trigger is a DOMAIN EVENT,
    /// evaluated on the outbox DISPATCH path after plugin subscribers, in the record-pinned
    /// scope. Conditions read the event PAYLOAD and (via row.*) the entity the payload
    /// references; the ONLY action is set-field — publish-event is forbidden at define (RUL007)
    /// so no rule can re-trigger a rule. The write rides the dispatcher's per-record
    /// SaveChanges. Isolated like a subscriber: a bad rule never wedges dispatch.
    /// </summary>
    internal static async Task ExecuteEventActionsAsync(
        DbContext db, string eventType, JsonElement payload, string tenantId,
        IExtensionRegistry registry, CancellationToken ct)
    {
        var rules = await db.Set<AutomationRuleEntity>()
            .Where(x => x.OnEvent == eventType && !x.Retired && x.ActionJson != null)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        if (rules.Count == 0) return;

        var specCache = new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            try
            {
                var firing = await EvaluateRuleAsync(
                    db, rule, payload, tenantId, trackRow: true, rowCache: null, ct);
                if (firing.RowMissing || !firing.Fired) continue;

                // RUL007 guarantees set-field only; re-validated against the current registry.
                var action = JsonSerializer.Deserialize<RuleAction>(rule.ActionJson!, TamJson.Options)!;
                if (action.Type == "set-field")
                    await TryApplySetFieldAsync(
                        specCache, registry, new TenantId(tenantId), rule, action, firing.Row, ct);
            }
            catch (Exception e) when (IsRuleFault(e))
            {
                // Isolated like a plugin subscriber — a broken rule never wedges dispatch.
            }
        }
        // The dispatcher's per-record SaveChanges commits any writes made here.
    }

    /// <summary>Applies a firing set-field to the tracked row — re-validating against the
    /// CURRENT registry (round 5, F1: a rule may not outlive a field's constraints). False
    /// when the target no longer accepts the write; the caller decides warn (operation seam)
    /// vs skip (dispatch seam).</summary>
    private static async Task<bool> TryApplySetFieldAsync(
        Dictionary<string, IReadOnlyList<ExtensionFieldSpec>> specCache, IExtensionRegistry registry,
        TenantId tenant, AutomationRuleEntity rule, RuleAction action, object? row, CancellationToken ct)
    {
        if (row is not IExtensible extensible || rule.RowEntityKey is not { } entityKey
            || action.Field is not { } field)
            return false;
        if (!specCache.TryGetValue(entityKey, out var specs))
            specCache[entityKey] = specs = await registry.For(tenant, entityKey, ct);
        var key = field.StartsWith("ext.", StringComparison.Ordinal) ? field["ext.".Length..] : field;
        var spec = specs.FirstOrDefault(s => s.Key == key);
        if (spec is null || RejectSetFieldValue(spec, action.Value) is not null) return false;
        var value = ConstValue(action.Value);
        extensible.Extensions = extensible.Extensions.WithValue(
            key, value is null ? null : spec.Semantic.Normalize(value));
        return true;
    }

    /// <summary>Wire input → Px field values: plain members by wire name, tenant/plugin/package
    /// extension fields as "ext.{key}" (reading the change-set's value).</summary>
    internal static object? FieldValue(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        JsonElement element;
        if (name.StartsWith("ext.", StringComparison.Ordinal))
        {
            if (!body.TryGetProperty("extensions", out var extensions)
                || extensions.ValueKind != JsonValueKind.Object
                || !extensions.TryGetProperty(name["ext.".Length..], out var change))
                return null;
            if (change.ValueKind == JsonValueKind.Object && change.TryGetProperty("value", out var value))
                element = value;
            else element = change;
        }
        else if (!body.TryGetProperty(name, out element))
        {
            return null;
        }

        // Compiled Change<T> members arrive as {original, value} envelopes exactly like
        // extension change-sets — unwrap them so edit-operation rules see the new value.
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("value", out var inner))
            element = inner;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetDecimal(out var number) ? number : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>The set-field guard shared by define AND execute (review round 5, F1): a rule
    /// may write only ACTIVE, non-ReadOnly registered fields, and the value must pass the
    /// field's own semantic type + options — exactly what ExtensionApplier and the packaged
    /// writer enforce. Null = writable; a string = why not. Clearing (null value) is allowed.</summary>
    internal static string? RejectSetFieldValue(ExtensionFieldSpec spec, JsonElement? value)
    {
        if (spec.ReadOnly)
            return $"'ext.{spec.Key}' is plugin-owned (read-only) — a rule may not set it";
        if (spec.State is not ExtensionFieldState.Active)
            return $"'ext.{spec.Key}' is not an active field";
        var raw = ConstValue(value);
        if (raw is null) return null;
        var normalized = spec.Semantic.Normalize(raw);
        if (spec.Semantic.Validate(normalized) is not null)
            return $"value for 'ext.{spec.Key}' fails its semantic type";
        if (spec.Options is { Count: > 0 } options && normalized is string s && !options.Contains(s))
            return $"value for 'ext.{spec.Key}' is not one of its declared options";
        return null;
    }

    private static object? ConstValue(JsonElement? value) => value is not { } v ? null : v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetDecimal(out var n) ? n : null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>The row.* namespace, made WIRE-IDENTICAL: the target row is loaded by primary
    /// key (entity resolved by wire key — no CLR coupling in the stored rule), tenant-checked
    /// explicitly (FindAsync bypasses the global filter, same lesson as the packaged writer),
    /// and serialized through TamJson so enums, wrappers and money compare exactly as they do
    /// on the wire. Null when the row (or a parsable id) is absent.</summary>
    private static async Task<(object? Row, JsonElement? Json)> RowAsync(
        DbContext db, string entityKey, object? idValue, string tenantId, CancellationToken ct)
    {
        if (idValue is not string idText || !Guid.TryParse(idText, out var id)) return (null, null);
        var entityType = db.Model.GetEntityTypes()
            .FirstOrDefault(t => TamModel.EntityKey(t.ClrType) == entityKey);
        if (entityType is null) return (null, null);

        var keyType = entityType.FindPrimaryKey()!.Properties.Single().ClrType;
        var keyValue = keyType == typeof(Guid) ? id : ValueWrapper.Wrap(keyType, id);
        var row = await db.FindAsync(entityType.ClrType, [keyValue], ct);
        if (row is null) return (null, null);
        if (row is ITenantScoped scoped && scoped.TenantId != tenantId) return (null, null);

        return (row, JsonSerializer.SerializeToElement(row, entityType.ClrType, TamJson.Options));
    }
}
