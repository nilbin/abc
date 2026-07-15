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
        plugin.Model
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
        foreach (var rule in rules)
        {
            bool fired;
            try
            {
                var condition = JsonSerializer.Deserialize<Px>(rule.ConditionJson, TamJson.Options)!;
                fired = PxBinary.Truthy(condition.Evaluate(name => FieldValue(body, name)));
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
        catch (JsonException)
        {
            condition = null;
        }
        if (condition is null || !OperatorsSupported(condition))
            return RuleFindings.InvalidCondition.At(nameof(Input.Condition));

        // RUL002: every field the condition references must exist on the operation's input —
        // compiled members by wire name, extension fields (tenant/plugin/package) as ext.{key}.
        var known = operation.InputFields.Select(f => f.WireName).ToHashSet(StringComparer.Ordinal);
        if (operation.ExtensibleEntity is { } entity)
        {
            var specs = await registry.For(context.TenantId, TamModel.EntityKey(entity), ct);
            foreach (var spec in specs) known.Add($"ext.{spec.Key}");
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
        rule.TargetField = input.TargetField;
        rule.MessagesJson = JsonSerializer.Serialize(input.Messages);
        rule.Retired = false;

        return new Output(rule.Id);
    }

    /// <summary>The closed operator set — a stored rule must never throw at evaluation time.</summary>
    private static bool OperatorsSupported(Px px) => px switch
    {
        PxConst or PxField => true,
        PxUnary u => u.Op is "not" or "isNull" or "isNotNull" && OperatorsSupported(u.X),
        PxBinary b => b.Op is "eq" or "ne" or "gt" or "ge" or "lt" or "le" or "and" or "or"
            && OperatorsSupported(b.L) && OperatorsSupported(b.R),
        _ => false,
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
