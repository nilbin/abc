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
            .Where(x => x.OnOperation == operationId && !x.Retired)
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
                            await RowJsonAsync(db, entityKey, FieldValue(body, idField), context, ct);
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

    /// <summary>The row.* namespace, made WIRE-IDENTICAL: the target row is loaded by primary
    /// key (entity resolved by wire key — no CLR coupling in the stored rule), tenant-checked
    /// explicitly (FindAsync bypasses the global filter, same lesson as the packaged writer),
    /// and serialized through TamJson so enums, wrappers and money compare exactly as they do
    /// on the wire. Null when the row (or a parsable id) is absent.</summary>
    private static async Task<JsonElement?> RowJsonAsync(
        DbContext db, string entityKey, object? idValue, OperationContext context, CancellationToken ct)
    {
        if (idValue is not string idText || !Guid.TryParse(idText, out var id)) return null;
        var entityType = db.Model.GetEntityTypes()
            .FirstOrDefault(t => TamModel.EntityKey(t.ClrType) == entityKey);
        if (entityType is null) return null;

        var keyType = entityType.FindPrimaryKey()!.Properties.Single().ClrType;
        var keyValue = keyType == typeof(Guid) ? id : ValueWrapper.Wrap(keyType, id);
        var row = await db.FindAsync(entityType.ClrType, [keyValue], ct);
        if (row is null) return null;
        if (row is ITenantScoped scoped && scoped.TenantId != context.TenantId.Value) return null;

        return JsonSerializer.SerializeToElement(row, entityType.ClrType, TamJson.Options);
    }
}

[Operation("rules.define")]
[Authorize("rules.manage")]
public static class DefineAutomationRule
{
    public sealed record Input(
        [property: LabelKey("labels.rule")] string Name,
        [property: LabelKey("labels.on-operation")] string OnOperation,
        [property: LabelKey("labels.condition")] string Condition,
        [property: LabelKey("labels.messages")] Dictionary<string, string> Messages,
        [property: LabelKey("labels.target-field")] string? TargetField = null);

    public sealed record Output(Guid RuleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model,
        IExtensionRegistry registry, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Name, "^[a-z][a-z0-9-]*$"))
            return ValidationFindings.InvalidValue.At(nameof(Input.Name));

        // RUL001: the trigger must be a compiled operation — and an inactive plugin's
        // operations do not exist for this tenant, exactly as the pipeline's 404 says.
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

        // RUL002: every field the condition references must exist on the operation's input —
        // compiled members by wire name, extension fields (tenant/plugin/package) as ext.{key}.
        var known = operation.InputFields.Select(f => f.WireName).ToHashSet(StringComparer.Ordinal);
        if (operation.ExtensibleEntity is { } entity)
        {
            var specs = await registry.For(context.TenantId, TamModel.EntityKey(entity), ct);
            foreach (var spec in specs) known.Add($"ext.{spec.Key}");
        }
        // docs/22 row.* increment: conditions may read the operation's TARGET row — one row,
        // resolved from the single input field named {entity}Id, verified here so evaluation
        // can never reference a field the entity does not have.
        string? rowEntityKey = null, rowIdField = null;
        var rowFields = condition.Fields().Where(f => f.StartsWith("row.", StringComparison.Ordinal))
            .Distinct().ToList();
        if (rowFields.Count > 0)
        {
            var inputNames = operation.InputFields.Select(f => f.WireName)
                .ToHashSet(StringComparer.Ordinal);
            var targets = tam.Db.Model.GetEntityTypes()
                .Select(t => t.ClrType)
                .Where(t => inputNames.Contains(Naming.Camel(t.Name) + "Id"))
                .ToList();
            // RUL004: creates, bulk ops, and anything without exactly ONE {entity}Id input
            // simply do not offer row.* — the wall is named at define time, not hit at runtime.
            if (targets.Count != 1)
                return RuleFindings.NoTargetRow.With(("operation", input.OnOperation))
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

        // RUL003: the rule's message is product surface — default culture is mandatory.
        if (!input.Messages.ContainsKey(model.DefaultCulture))
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
        rule.OnOperation = input.OnOperation;
        rule.ConditionJson = input.Condition;
        rule.RowEntityKey = rowEntityKey;
        rule.RowIdField = rowIdField;
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
