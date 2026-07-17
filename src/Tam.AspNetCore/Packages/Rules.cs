using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.AspNetCore.SystemOps;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Tenant automation rules (docs/22 P5): declarative Px conditions as data.</summary>
[TamPackage("tam.rules", "rules", "web.rules")]
public sealed class TamRulesPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        // Nav CONTENT + suggestion (docs/30 D-N2) — the host owns placement.
        plugin.Nav(nav => nav.Page("rules", grid: "web.rules", suggest: "administration", order: 60));
        // The evaluator IS a gate: pure-over-input, pre-transaction, target set = rule rows.
        // The executor has no rules special case — the P5 feature dogfoods the gate seam.
        plugin.GateAll<RulesGate>(pure: true);
        // Action rules WRITE (set-field, publish-event), so they run in the transactional
        // phase — same seam, second registration; findings stay the cheap pure fail.
        plugin.GateAll<RuleActionsGate>();
        plugin
            .AddOperationType(typeof(DefineAutomationRule))
            .AddOperationType(typeof(RetireRule))
            .AddViewType(typeof(RuleList))
            .Form<DefineAutomationRule.Input>("web.rules.define", "rules.define", form =>
            {
                form.Field(x => x.Name);
                form.Field(x => x.OnOperation);
                form.Field(x => x.Condition).Renderer("multiline");
                form.Field(x => x.Messages).Renderer("culture-text");
                form.Field(x => x.TargetField);
                form.Field(x => x.Action).Renderer("multiline");
            })
            .Grid<RuleList.Result>("web.rules", "rules.list", grid =>
            {
                grid.Column(x => x.Name);
                grid.Column(x => x.OnOperation);
                grid.Column(x => x.Retired);
                grid.ToolbarAction("rules.define");
                grid.RowAction("rules.retire");
            });
    }
}

public static class RuleFindings
{
    public static readonly FindingFactory UnknownOperation = Finding.Error("rules.unknown-operation"); // RUL001
    public static readonly FindingFactory UnknownField = Finding.Error("rules.unknown-field");         // RUL002
    public static readonly FindingFactory MissingMessage = Finding.Error("rules.missing-message");     // RUL003
    public static readonly FindingFactory NoTargetRow = Finding.Error("rules.no-target-row");           // RUL004
    public static readonly FindingFactory InvalidAction = Finding.Error("rules.invalid-action");         // RUL005
    public static readonly FindingFactory InvalidCondition = Finding.Error("rules.invalid-condition");
}

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
/// bounded — never a string-parsed expression. The action set is closed: v1 is the blocking
/// finding (the Salesforce validation rule); set-field / publish-event follow the same shape.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>
    /// Evaluates the tenant's active rules for this operation against the wire input.
    /// A firing rule contributes a blocking finding whose message is the rule's own
    /// tenant-authored text in the request culture (code = "rules.{name}", D6 as data).
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
            bool fired;
            try
            {
                var condition = JsonSerializer.Deserialize<Px>(rule.ConditionJson, TamJson.Options)!;
                // docs/22 row.* increment: hydrate the operation's target row once per
                // (entity, id) — read-only, pre-transaction, tenant-checked. A missing row
                // means the rule does not FIRE (the pipeline's own not-found follows); only
                // a rule that cannot EVALUATE warns.
                JsonElement? row = null;
                if (rule is { RowEntityKey: { } entityKey, RowIdField: { } idField })
                {
                    var cacheKey = $"{entityKey}|{FieldValue(body, idField)}";
                    if (!rowCache.TryGetValue(cacheKey, out row))
                        rowCache[cacheKey] = row =
                            await RowJsonAsync(db, entityKey, FieldValue(body, idField), context.TenantId.Value, ct);
                    if (row is null) continue;
                }
                fired = PxBinary.Truthy(condition.Evaluate(name =>
                    name.StartsWith("row.", StringComparison.Ordinal)
                        ? row is { } r ? FieldValue(r, name["row.".Length..]) : null
                        : FieldValue(body, name)));
            }
            catch (Exception e) when (e is JsonException or NotSupportedException
                or ArgumentException or FormatException or InvalidOperationException)
            {
                // A rule that cannot evaluate must never brick the operation (fail-open would
                // silently skip validation; fail-closed would block the tenant) — surface a
                // non-blocking warning naming the rule so the admin can fix it.
                findings.Add(Finding.Warning("rules.evaluation-failed")
                    .With(("rule", rule.Name)));
                continue;
            }
            if (!fired) continue;

            var messages = JsonSerializer.Deserialize<Dictionary<string, string>>(rule.MessagesJson) ?? [];
            var finding = Finding.Error($"rules.{rule.Name}").Create() with
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
            };
            findings.Add(finding);
        }
        return findings;
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

    internal sealed record RuleAction(string Type, string? Field = null, JsonElement? Value = null);

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
                var condition = JsonSerializer.Deserialize<Px>(rule.ConditionJson, TamJson.Options)!;
                object? row = null;
                JsonElement? rowJson = null;
                if (rule is { RowEntityKey: { } entityKey, RowIdField: { } idField })
                {
                    (row, rowJson) = await RowAsync(db, entityKey, FieldValue(body, idField), context.TenantId.Value, ct);
                    if (rowJson is null) continue;   // gone → the pipeline's not-found follows
                }
                var fired = PxBinary.Truthy(condition.Evaluate(name =>
                    name.StartsWith("row.", StringComparison.Ordinal)
                        ? rowJson is { } r ? FieldValue(r, name["row.".Length..]) : null
                        : FieldValue(body, name)));
                if (!fired) continue;

                var action = JsonSerializer.Deserialize<RuleAction>(rule.ActionJson!, TamJson.Options)!;
                switch (action.Type)
                {
                    case "set-field" when row is IExtensible extensible
                            && rule.RowEntityKey is { } ek && action.Field is { } field:
                        // Re-validate against the CURRENT registry, not the define-time snapshot
                        // (review round 5, F1): a rule may not outlive a field's constraints. A
                        // now-invalid target degrades to the same warning as an unevaluable rule.
                        if (!specCache.TryGetValue(ek, out var specs))
                            specCache[ek] = specs = await registry.For(context.TenantId, ek, ct);
                        var key = field.StartsWith("ext.", StringComparison.Ordinal) ? field["ext.".Length..] : field;
                        var spec = specs.FirstOrDefault(s => s.Key == key);
                        if (spec is null || RejectSetFieldValue(spec, action.Value) is not null)
                        {
                            findings.Add(Finding.Warning("rules.evaluation-failed").With(("rule", rule.Name)));
                            break;
                        }
                        var value = ConstValue(action.Value);
                        extensible.Extensions = extensible.Extensions.WithValue(
                            key, value is null ? null : spec.Semantic.Normalize(value));
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
            catch (Exception e) when (e is JsonException or NotSupportedException
                or ArgumentException or FormatException or InvalidOperationException)
            {
                findings.Add(Finding.Warning("rules.evaluation-failed")
                    .With(("rule", rule.Name)));
            }
        }
        return findings;
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
    private static async Task<JsonElement?> RowJsonAsync(
        DbContext db, string entityKey, object? idValue, string tenantId, CancellationToken ct)
    {
        // Pure phase (finding conditions): DETACH the row after reading so the pre-transaction
        // load never lingers in the identity map — the handler and the transactional action
        // gate re-read it fresh inside the transaction rather than seeing this stale snapshot
        // (review round 5, F3). The action gate's RowAsync keeps its own tracked read.
        var (row, json) = await RowAsync(db, entityKey, idValue, tenantId, ct);
        if (row is not null) db.Entry(row).State = EntityState.Detached;
        return json;
    }

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
                var condition = JsonSerializer.Deserialize<Px>(rule.ConditionJson, TamJson.Options)!;
                object? row = null;
                JsonElement? rowJson = null;
                if (rule is { RowEntityKey: { } ek, RowIdField: { } idField })
                {
                    (row, rowJson) = await RowAsync(db, ek, FieldValue(payload, idField), tenantId, ct);
                    if (rowJson is null) continue;   // the referenced row is gone
                }
                var fired = PxBinary.Truthy(condition.Evaluate(name =>
                    name.StartsWith("row.", StringComparison.Ordinal)
                        ? rowJson is { } r ? FieldValue(r, name["row.".Length..]) : null
                        : FieldValue(payload, name)));
                if (!fired) continue;

                // RUL007 guarantees set-field only. Re-validate against the current registry.
                var action = JsonSerializer.Deserialize<RuleAction>(rule.ActionJson!, TamJson.Options)!;
                if (action.Type != "set-field" || row is not IExtensible extensible
                    || rule.RowEntityKey is not { } entityKey || action.Field is not { } field)
                    continue;
                if (!specCache.TryGetValue(entityKey, out var specs))
                    specCache[entityKey] = specs = await registry.For(new TenantId(tenantId), entityKey, ct);
                var key = field.StartsWith("ext.", StringComparison.Ordinal) ? field["ext.".Length..] : field;
                var spec = specs.FirstOrDefault(s => s.Key == key);
                if (spec is null || RejectSetFieldValue(spec, action.Value) is not null) continue;
                var value = ConstValue(action.Value);
                extensible.Extensions = extensible.Extensions.WithValue(
                    key, value is null ? null : spec.Semantic.Normalize(value));
            }
            catch (Exception e) when (e is JsonException or NotSupportedException
                or ArgumentException or FormatException or InvalidOperationException)
            {
                // Isolated like a plugin subscriber — a broken rule never wedges dispatch.
            }
        }
        // The dispatcher's per-record SaveChanges commits any writes made here.
    }
}

[Operation("rules.define")]
[Authorize("rules.manage")]
public static class DefineAutomationRule
{
    public sealed record Input(
        [property: LabelKey("labels.rule")] string Name,
        [property: LabelKey("labels.on-operation")] string? OnOperation,
        [property: LabelKey("labels.condition")] string Condition,
        [property: LabelKey("labels.messages")] Dictionary<string, string> Messages,
        [property: LabelKey("labels.target-field")] string? TargetField = null,
        [property: LabelKey("labels.action")] string? Action = null,
        [property: LabelKey("labels.on-event")] string? OnEvent = null);

    public sealed record Output(Guid RuleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model,
        IExtensionRegistry registry, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Name, "^[a-z][a-z0-9-]*$"))
            return ValidationFindings.InvalidValue.At(nameof(Input.Name));

        // Bound the stored/re-parsed payloads (review round 5): a condition/action is
        // deserialized on every triggering operation, so an unbounded blob is a self-DoS.
        // Generous ceilings — real rules are tiny; this only stops abuse.
        if (input.Condition.Length > 8_000)
            return ValidationFindings.TooLong.With(("max", 8_000)).At(nameof(Input.Condition));
        if (input.Action is { Length: > 2_000 })
            return ValidationFindings.TooLong.With(("max", 2_000)).At(nameof(Input.Action));

        // Exactly one trigger: an OPERATION (evaluated in the pipeline) or a domain EVENT
        // (evaluated on the outbox dispatch path — docs/22 effect-triggered rules).
        var onEvent = string.IsNullOrEmpty(input.OnEvent) ? null : input.OnEvent;
        if ((onEvent is null) == string.IsNullOrEmpty(input.OnOperation))
            return ValidationFindings.InvalidValue
                .With(("detail", "set exactly one of onOperation or onEvent")).At(nameof(Input.Name));

        // triggerFields: the field wire names a condition may reference at the TOP level.
        // Operation → input fields (+ the extensible entity's tenant fields); event → the
        // declared payload fields. Row fields (row.*) are validated separately, below.
        HashSet<string> triggerFields;
        if (onEvent is null)
        {
            // RUL001: the trigger must be a compiled operation — an inactive plugin's operations
            // do not exist for this tenant, exactly as the pipeline's 404 says.
            if (!model.Operations.TryGetValue(input.OnOperation, out var operation))
                return RuleFindings.UnknownOperation.With(("operation", input.OnOperation))
                    .At(nameof(Input.OnOperation));
            if (operation.Plugin is { } plugin)
            {
                var active = await PluginActivations.ActiveAsync(tam.Db, context.TenantId.Value, ct);
                if (!active.Contains(plugin))
                    return RuleFindings.UnknownOperation.With(("operation", input.OnOperation))
                        .At(nameof(Input.OnOperation));
            }
            triggerFields = operation.InputFields.Select(f => f.WireName).ToHashSet(StringComparer.Ordinal);
            if (operation.ExtensibleEntity is { } entity)
                foreach (var spec in await registry.For(context.TenantId, TamModel.EntityKey(entity), ct))
                    triggerFields.Add($"ext.{spec.Key}");
        }
        else
        {
            // RUL001-event: the trigger must be a DECLARED domain event, active for the tenant,
            // and NEVER a `rules.*` event (RUL006) — that plus set-field-only (RUL007, below)
            // makes a rule → event → rule cycle structurally impossible.
            if (onEvent.StartsWith("rules.", StringComparison.Ordinal))
                return RuleFindings.InvalidAction
                    .With(("detail", "a rule may not trigger on a 'rules.*' event")).At(nameof(Input.OnEvent));
            if (!model.Events.TryGetValue(onEvent, out var declared))
                return RuleFindings.UnknownOperation.With(("operation", onEvent)).At(nameof(Input.OnEvent));
            if (declared.Plugin is { } evPlugin && !model.Packages.ContainsKey(evPlugin))
            {
                var active = await PluginActivations.ActiveAsync(tam.Db, context.TenantId.Value, ct);
                if (!active.Contains(evPlugin))
                    return RuleFindings.UnknownOperation.With(("operation", onEvent)).At(nameof(Input.OnEvent));
            }
            triggerFields = declared.Fields.ToHashSet(StringComparer.Ordinal);
        }

        // The condition is structured Px, stored as authored — a parse failure is a finding,
        // and user data only ever lands in const nodes.
        Px? condition;
        try
        {
            condition = JsonSerializer.Deserialize<Px>(input.Condition, TamJson.Options);
        }
        // NotSupportedException is STJ's polymorphic-discriminator failure — the shape a
        // hand-authored condition most often gets wrong (RTFM #3: this used to 500).
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            condition = null;
        }
        if (condition is null)
            return RuleFindings.InvalidCondition
                .With(("detail", """condition must be a Px node: {"t":"bin|un|field|const", ...}"""))
                .At(nameof(Input.Condition));
        if (FirstUnsupported(condition) is { } bad)
            return RuleFindings.InvalidCondition.With(("op", bad)).At(nameof(Input.Condition));

        // RUL002: every top-level field the condition references must be a trigger field.
        var known = new HashSet<string>(triggerFields, StringComparer.Ordinal);

        // docs/22 action catalog: the closed action set, validated as data. Null = the
        // blocking finding; set-field targets the trigger's row, publish-event derives its
        // event type from the rule name (never a chosen id — no collision with contracts).
        RuleEvaluator.RuleAction? action = null;
        if (input.Action is { Length: > 0 })
        {
            try
            {
                action = JsonSerializer.Deserialize<RuleEvaluator.RuleAction>(input.Action, TamJson.Options);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException) { }
            if (action is null || action.Type is not ("set-field" or "publish-event"))
                return RuleFindings.InvalidAction
                    .With(("detail", "action.type must be set-field or publish-event"))
                    .At(nameof(Input.Action));
            if (action.Type == "publish-event" && model.Events.ContainsKey($"rules.{input.Name}"))
                return RuleFindings.InvalidAction
                    .With(("detail", $"event 'rules.{input.Name}' collides with a compiled contract"))
                    .At(nameof(Input.Action));
        }
        // RUL007: an EVENT rule must DO something (a finding post-commit blocks nothing) and may
        // only set-field — publish-event from an effect rule could re-trigger rules (loop).
        if (onEvent is not null && action?.Type != "set-field")
            return RuleFindings.InvalidAction
                .With(("detail", "an event-triggered rule must carry a set-field action"))
                .At(nameof(Input.Action));

        // docs/22 row.* increment: conditions may read the operation's TARGET row — one row,
        // resolved from the single input field named {entity}Id, verified here so evaluation
        // can never reference a field the entity does not have.
        string? rowEntityKey = null, rowIdField = null;
        var rowFields = condition.Fields().Where(f => f.StartsWith("row.", StringComparison.Ordinal))
            .Distinct().ToList();
        if (rowFields.Count > 0 || action?.Type == "set-field")
        {
            var targets = tam.Db.Model.GetEntityTypes()
                .Select(t => t.ClrType)
                .Where(t => triggerFields.Contains(Naming.Camel(t.Name) + "Id"))
                .ToList();
            // RUL004: a trigger without exactly ONE {entity}Id field (creates, bulk ops, or an
            // event whose payload carries no id) does not offer row.* / set-field — named here.
            if (targets.Count != 1)
                return RuleFindings.NoTargetRow.With(("operation", input.OnOperation + input.OnEvent))
                    .At(nameof(Input.Condition));
            var rowEntity = targets[0];
            rowEntityKey = TamModel.EntityKey(rowEntity);
            rowIdField = Naming.Camel(rowEntity.Name) + "Id";

            // RUL002 over the row namespace: compiled members by camel name, extension fields
            // as row.ext.{key} against the tenant's registry for that entity.
            var rowKnown = rowEntity.GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => Naming.Camel(p.Name)).ToHashSet(StringComparer.Ordinal);
            var rowSpecs = await registry.For(context.TenantId, rowEntityKey, ct);
            foreach (var spec in rowSpecs) rowKnown.Add($"ext.{spec.Key}");
            foreach (var f in rowFields.Where(f => rowKnown.Contains(f["row.".Length..])))
                known.Add(f);

            // RUL005 for set-field: the target must be one of the entity's REGISTERED, WRITABLE
            // extension fields (compiled state stays behind intents — EDIT001 extended to
            // tenants), and the value must pass that field's own semantic type + options —
            // exactly the checks the wire channel and the packaged-field writer enforce
            // (review round 5, F1). Validating here AND at execute (below) means a rule can
            // never write past a field's constraints, even after the registry changes.
            if (action?.Type == "set-field")
            {
                var key = action.Field is { } fieldRef && fieldRef.StartsWith("ext.", StringComparison.Ordinal)
                    ? fieldRef["ext.".Length..] : null;
                var spec = key is null ? null : rowSpecs.FirstOrDefault(s => s.Key == key);
                if (spec is null)
                    return RuleFindings.InvalidAction
                        .With(("detail", $"set-field targets 'ext.{{key}}' on '{rowEntityKey}'; known: "
                            + string.Join(", ", rowSpecs.Select(s => $"ext.{s.Key}"))))
                        .At(nameof(Input.Action));
                if (RuleEvaluator.RejectSetFieldValue(spec, action.Value) is { } why)
                    return RuleFindings.InvalidAction.With(("detail", why)).At(nameof(Input.Action));
            }
        }
        var unknown = condition.Fields().Distinct().Where(f => !known.Contains(f)).ToList();
        if (unknown.Count > 0)
        {
            return new Result<Output>
            {
                Findings = unknown.Select(f => RuleFindings.UnknownField
                    .With(("field", f)).At(nameof(Input.Condition))).ToList(),
            };
        }
        if (input.TargetField is { Length: > 0 } target && !known.Contains(target))
            return RuleFindings.UnknownField.With(("field", target)).At(nameof(Input.TargetField));

        // RUL003: a FINDING rule's message is product surface — default culture is mandatory.
        // Action rules produce no blocking text; their messages are optional.
        if (action is null && !input.Messages.ContainsKey(model.DefaultCulture))
            return RuleFindings.MissingMessage.At(nameof(Input.Messages));

        var rule = await tam.Db.Set<AutomationRuleEntity>().SingleOrDefaultAsync(
            x => x.Name == input.Name, ct);
        if (rule is null)
        {
            rule = new AutomationRuleEntity
            {
                Id = Guid.NewGuid(),
                Name = input.Name,
            };
            tam.Db.Add(rule);
        }
        rule.OnOperation = input.OnOperation ?? "";
        rule.OnEvent = onEvent;
        rule.ConditionJson = input.Condition;
        rule.RowEntityKey = rowEntityKey;
        rule.RowIdField = rowIdField;
        rule.ActionJson = action is null ? null : input.Action;
        rule.TargetField = input.TargetField;
        rule.MessagesJson = JsonSerializer.Serialize(input.Messages);
        rule.Retired = false;

        return new Output(rule.Id);
    }

    /// <summary>The closed operator set — a stored rule must never throw at evaluation time.
    /// Returns the FIRST unsupported operator so the finding can name it (RTFM #3: a bare
    /// invalid-condition left tenant-tool authors probing blind).</summary>
    private static string? FirstUnsupported(Px px) => px switch
    {
        PxConst or PxField => null,
        PxFn f => f.Op is "today" ? null : f.Op,
        PxUnary u => u.Op is "not" or "isNull" or "isNotNull" ? FirstUnsupported(u.X) : u.Op,
        PxBinary b => b.Op is "eq" or "ne" or "gt" or "ge" or "lt" or "le" or "and" or "or"
            ? FirstUnsupported(b.L) ?? FirstUnsupported(b.R)
            : b.Op,
        _ => px.GetType().Name,
    };
}

[Operation("rules.retire")]
[Authorize("rules.manage")]
public static class RetireRule
{
    public sealed record Input([property: LabelKey("labels.rule")] string Name);

    public sealed record Output(string Name);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var rule = await tam.Db.Set<AutomationRuleEntity>().SingleOrDefaultAsync(
            x => x.Name == input.Name, ct);
        if (rule is null) return PipelineFindings.NotFound.Create();

        rule.Retired = true;
        return new Output(rule.Name);
    }
}

[View("rules.list")]
[Authorize("rules.manage")]
public static class RuleList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.rule")]
        public string Name { get; init; } = "";
        [LabelKey("labels.on-operation")]
        public string OnOperation { get; init; } = "";
        [LabelKey("labels.retired")]
        public bool Retired { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var rules = tam.Db.Set<AutomationRuleEntity>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
            rules = rules.Where(x => x.Name.Contains(query.Search!));
        return rules.Select(x => new Result
        {
            Id = x.Id, Name = x.Name, OnOperation = x.OnOperation, Retired = x.Retired,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name));
}
