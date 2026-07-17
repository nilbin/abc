using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Tenant automation rules (docs/22 P5): declarative Px conditions as data. This
/// file is the tam.rules SURFACE (operations, views, form, grid, derivations); the engine —
/// evaluator and both gates — is core infrastructure in RuleEngine.cs, per the tier's split
/// rule ("the service is core; the package is only the surface").</summary>
[TamPackage("tam.rules", "rules", "web.rules")]
public sealed class TamRulesPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
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
            .AddViewType(typeof(RuleSchema))
            .AddDerivationHost(typeof(RuleDefineDerivations))
            .Form<DefineAutomationRule.Input>("web.rules.define", "rules.define", form =>
            {
                form.Field(x => x.Name);
                // Trigger: an operation OR a domain event. "Exactly one" is the mutual ResetOn
                // pair (docs/05); the pickers themselves are plain searchable selects.
                form.Field(x => x.OnOperation).Renderer("rule-trigger-operation")
                    .ResetOn(x => x.OnEvent);
                form.Field(x => x.OnEvent).Renderer("rule-trigger-event")
                    .ResetOn(x => x.OnOperation);
                // The visual builders (docs/22): the condition/action editors resolve their
                // referenceable fields, operators and value options from the chosen trigger via
                // the rules.schema view — server-authoritative typing, no hand-authored Px JSON.
                // Everything below the trigger is gated on it with the form's OWN dynamics —
                // VisibleWhen/RequiredWhen Px, ResetOn (authoring against the OLD trigger's
                // fields must not survive a trigger change), and a server derivation for
                // TargetField's options (docs/05, dogfooded).
                form.Field(x => x.Condition).Renderer("rule-condition")
                    .VisibleWhen(x => x.OnOperation != null || x.OnEvent != null)
                    .ResetOn(x => x.OnOperation, x => x.OnEvent);
                form.Field(x => x.Messages).Renderer("culture-text")
                    .VisibleWhen(x => x.OnOperation != null || x.OnEvent != null)
                    // RUL003 as form state: a FINDING rule (no action) must carry a message.
                    .RequiredWhen(x => x.Action == null);
                form.Field(x => x.TargetField)
                    // The finding's anchor field — meaningless for action rules.
                    .VisibleWhen(x => (x.OnOperation != null || x.OnEvent != null) && x.Action == null)
                    .ResetOn(x => x.OnOperation, x => x.OnEvent);
                form.Field(x => x.Action).Renderer("rule-action")
                    .VisibleWhen(x => x.OnOperation != null || x.OnEvent != null)
                    .ResetOn(x => x.OnOperation, x => x.OnEvent);
            })
            .Grid<RuleList.Result>("web.rules", "rules.list", grid =>
            {
                grid.Column(x => x.Name);
                grid.Column(x => x.OnOperation);
                grid.Column(x => x.Retired);
                grid.ToolbarAction("rules.define");
                // Edit: rules.define is an upsert by name — the RowForm opens it prefilled
                // from the row (the Result carries the full definition for exactly this).
                grid.RowForm("rules.define");
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
    public static readonly FindingFactory InvalidName = Finding.Error("rules.invalid-name");
}

[Operation("rules.define")]
[Authorize("rules.manage")]
public static class DefineAutomationRule
{
    public sealed record Input(
        [property: LabelKey("labels.rule")] string Name,
        string? OnOperation,
        string Condition,
        // Optional at the wire (action rules carry no blocking text); RUL003 still demands the
        // default culture for FINDING rules — the form mirrors that with RequiredWhen.
        Dictionary<string, string>? Messages = null,
        string? TargetField = null,
        string? Action = null,
        string? OnEvent = null);

    public sealed record Output(Guid RuleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model,
        IExtensionRegistry registry, CancellationToken ct)
    {
        if (!Naming.IsSlug(input.Name))
            return RuleFindings.InvalidName.At(nameof(Input.Name));

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
        // A comparison against the null CONSTANT is (almost) always an unfinished builder
        // clause — and isNull/isNotNull exist for the real "is empty" intent. Refuse with the
        // redirect rather than store a rule that silently means something else.
        if (HasNullComparison(condition))
            return RuleFindings.InvalidCondition
                .With(("detail", "a comparison against null — use isNull/isNotNull"))
                .At(nameof(Input.Condition));

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
        if (action is null && input.Messages?.ContainsKey(model.DefaultCulture) is not true)
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
        rule.MessagesJson = JsonSerializer.Serialize(input.Messages ?? new Dictionary<string, string>());
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

    private static bool HasNullComparison(Px px) => px switch
    {
        PxBinary { Op: "eq" or "ne" or "gt" or "ge" or "lt" or "le" } b
            when IsNullConst(b.L) || IsNullConst(b.R) => true,
        PxBinary b => HasNullComparison(b.L) || HasNullComparison(b.R),
        PxUnary u => HasNullComparison(u.X),
        _ => false,
    };

    private static bool IsNullConst(Px px) => px is PxConst c
        && (c.V is null || c.V is JsonElement { ValueKind: JsonValueKind.Null });
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

    /// <summary>Carries the FULL definition, not just the grid columns: rules.define is an
    /// upsert by name, so the grid's RowForm edits a rule by prefilling the define form from
    /// the row — the result fields are named exactly like the operation's input fields.</summary>
    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.rule")]
        public string Name { get; init; } = "";
        public string OnOperation { get; init; } = "";
        public string? OnEvent { get; init; }
        public string Condition { get; init; } = "";
        public Dictionary<string, string> Messages { get; init; } = [];
        public string? TargetField { get; init; }
        public string? Action { get; init; }
        public bool Retired { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var rules = tam.Db.Set<AutomationRuleEntity>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
            rules = rules.Where(x => x.Name.Contains(query.Search!));
        // Materialized: the messages map deserializes per row, which SQL cannot project — and a
        // tenant's rule table is small config data, like rules.schema's computed rows.
        return rules.AsEnumerable().Select(x => new Result
        {
            Id = x.Id,
            Name = x.Name,
            OnOperation = x.OnOperation,
            OnEvent = x.OnEvent,
            Condition = x.ConditionJson,
            Messages = JsonSerializer.Deserialize<Dictionary<string, string>>(x.MessagesJson) ?? [],
            TargetField = x.TargetField,
            Action = x.ActionJson,
            Retired = x.Retired,
        }).AsQueryable();
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name));
}

/// <summary>
/// The rule-builder's server-authoritative schema (docs/22): given the chosen trigger, the ONE
/// thing the manifest cannot supply — the TARGET ROW entity's identity and its compiled field
/// types. The client already holds the trigger's input/payload fields and every entity's
/// extension fields (the manifest), and derives operators + value controls from each field's
/// wireKind; only the row.* namespace needs the server, because compiled entity property types
/// are not in the manifest. Returns one Result per referenceable compiled row field, or nothing
/// when the trigger has no single {entity}Id target row (creates, bulk ops, id-less events) —
/// the same RUL004 shape the define operation enforces, so the builder offers exactly what a
/// rule may reference. Pure, synchronous, tenant-agnostic (compiled model only): extension typing
/// stays in the manifest overlay where it already lives.
/// </summary>
[View("rules.schema")]
[Authorize("rules.manage")]
public static class RuleSchema
{
    /// <param name="Trigger">Operation id or event type to describe.</param>
    /// <param name="Kind">"operation" (default) or "event".</param>
    public sealed record Query(string? Trigger = null, string? Kind = null);

    public sealed record Result
    {
        /// <summary>The Px field path a condition references, e.g. "row.status".</summary>
        public string Path { get; init; } = "";
        /// <summary>Catalog key for the field's label (client localizes; falls back to the path).</summary>
        public string LabelKey { get; init; } = "";
        /// <summary>The field's wire kind — the client maps it to operators and a value control.</summary>
        public string WireKind { get; init; } = "";
        /// <summary>Enum/selection values, empty for free values.</summary>
        public IReadOnlyList<string> Options { get; init; } = [];
        /// <summary>The target row entity's key — the client pulls its extension fields
        /// (row.ext.* references and set-field targets) from the manifest overlay.</summary>
        public string EntityKey { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, TamModel model, ITamDb tam)
    {
        var empty = Enumerable.Empty<Result>().AsQueryable();
        if (string.IsNullOrEmpty(query.Trigger)) return empty;

        // The field names the trigger offers at the TOP level — used only to find the {entity}Id
        // that names the target row (the reference typing itself is the manifest's job).
        HashSet<string> triggerFields;
        if (string.Equals(query.Kind, "event", StringComparison.Ordinal))
        {
            if (!model.Events.TryGetValue(query.Trigger, out var declared)) return empty;
            triggerFields = declared.Fields.ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            if (!model.Operations.TryGetValue(query.Trigger, out var operation)) return empty;
            triggerFields = operation.InputFields.Select(f => f.WireName).ToHashSet(StringComparer.Ordinal);
        }

        // RUL004 mirror: exactly one {entity}Id names the target row; otherwise there is no
        // row.* namespace and no set-field, so the builder offers neither.
        var targets = tam.Db.Model.GetEntityTypes()
            .Select(t => t.ClrType)
            .Where(t => triggerFields.Contains(Naming.Camel(t.Name) + "Id"))
            .Distinct()
            .ToList();
        if (targets.Count != 1) return empty;
        var rowEntity = targets[0];
        var entityKey = TamModel.EntityKey(rowEntity);

        // The compiled scalar properties, typed exactly as operation/view fields are (same
        // FieldModel path), so wireKind, enum options and label keys match everything else the
        // client renders. Non-scalar members (navigations, collections, the extension bag) never
        // JSON-serialize to a comparable value, so they are not offered.
        var nullability = new System.Reflection.NullabilityInfoContext();
        var results = rowEntity.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && IsScalar(p.PropertyType))
            .Select(p =>
            {
                var field = FieldModel.From(p, nullability);
                return new Result
                {
                    Path = "row." + field.WireName,
                    LabelKey = field.LabelKey,
                    WireKind = field.Semantic.WireKind,
                    Options = field.EnumOptions ?? [],
                    EntityKey = entityKey,
                };
            })
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ToList();
        return results.AsQueryable();
    }

    /// <summary>A property whose value JSON-serializes to something a Px condition can compare:
    /// primitives, enums, dates, and single-value wrappers over them (Nullable unwrapped).
    /// Navigations, collections and the extension bag are excluded.</summary>
    private static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (ValueWrapper.IsWrapper(t)) t = ValueWrapper.UnderlyingType(t) ?? t;
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsEnum
            || t == typeof(string) || t == typeof(bool) || t == typeof(Guid)
            || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(decimal) || t == typeof(double) || t == typeof(float)
            || t == typeof(DateOnly) || t == typeof(DateTimeOffset) || t == typeof(DateTime)
            || t == typeof(TimeOnly);
    }
}

/// <summary>
/// Server derivations for the rules.define form (docs/05, dogfooded): TargetField — the field a
/// FINDING anchors to on the triggering operation's form — offers exactly the trigger's own
/// top-level fields as OPTIONS, localized, recomputed when the trigger changes. Only top-level
/// fields: a finding's target must anchor to a rendered form control, which row.* paths are not.
/// </summary>
public static class RuleDefineDerivations
{
    [ServerDerivation("rules.define.target-fields")]
    [DependsOn(nameof(DefineAutomationRule.Input.OnOperation), nameof(DefineAutomationRule.Input.OnEvent))]
    public static async Task<DerivationResult> TargetFields(
        DefineAutomationRule.Input input, DerivationContext context, TamModel model,
        IExtensionRegistry registry, CancellationToken ct)
    {
        var culture = context.Operation.Culture;
        var catalog = model.Locales.Catalog(culture);
        var fallback = model.Locales.Catalog(model.DefaultCulture);
        string Label(string key, string path) =>
            catalog.GetValueOrDefault(key) ?? fallback.GetValueOrDefault(key) ?? path;

        var options = new List<Option>();
        if (input.OnEvent is { Length: > 0 } onEvent)
        {
            // Event payloads declare names only — the name is the label.
            if (model.Events.TryGetValue(onEvent, out var declared))
                options.AddRange(declared.Fields.Select(f => new Option(f, f)));
        }
        else if (input.OnOperation is { Length: > 0 } onOperation
            && model.Operations.TryGetValue(onOperation, out var operation))
        {
            options.AddRange(operation.InputFields
                .Select(f => new Option(f.WireName, Label(f.LabelKey, f.WireName))));
            if (operation.ExtensibleEntity is { } entity)
            {
                foreach (var spec in await registry.For(
                    context.Operation.TenantId, TamModel.EntityKey(entity), ct))
                {
                    options.Add(new Option($"ext.{spec.Key}",
                        spec.Labels.GetValueOrDefault(culture)
                        ?? spec.Labels.Values.FirstOrDefault() ?? spec.Key));
                }
            }
        }
        return options.Count == 0
            ? DerivationResult.Empty
            : DerivationResult.Empty.AddOptions(nameof(DefineAutomationRule.Input.TargetField), options);
    }
}
