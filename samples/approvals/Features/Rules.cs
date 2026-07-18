using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Approvals;

public static class RuleFindings
{
    public static readonly FindingFactory UnknownTarget = Finding.Error("approvals.unknown-target");
    public static readonly FindingFactory NotGateable = Finding.Error("approvals.not-gateable");
    public static readonly FindingFactory UnknownRule = Finding.Error("approvals.unknown-rule");
}


[Operation("approvals.rules.define")]
[Authorize("approvals.manage")]
public static class DefineRule
{
    public sealed record Input(
        [property: LabelKey("approvals.labels.operation")] string OperationId,
        [property: LabelKey("approvals.labels.group")] Guid GroupId,
        [property: LabelKey("approvals.labels.threshold-field")] string? ThresholdField = null,
        [property: LabelKey("approvals.labels.threshold")] decimal? Threshold = null);

    public sealed record Output(Guid RuleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        // The rule targets a host WIRE id — validated against the compiled model so a typo
        // fails at definition, not silently-never at the gate.
        if (!model.Operations.ContainsKey(input.OperationId))
            return RuleFindings.UnknownTarget.At(nameof(Input.OperationId));
        // Never gate our own surface: an approval rule on approvals.approve would deadlock
        // every release behind a release.
        if (input.OperationId.StartsWith("approvals.", StringComparison.Ordinal))
            return RuleFindings.NotGateable.At(nameof(Input.OperationId));
        if (!await tam.Db.Set<ApprovalGroup>().AnyAsync(g => g.Id == input.GroupId, ct))
            return GroupFindings.UnknownGroup.At(nameof(Input.GroupId));

        var rule = new ApprovalRule
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            OperationId = input.OperationId,
            GroupId = input.GroupId,
            ThresholdField = input.ThresholdField,
            Threshold = input.Threshold,
        };
        tam.Db.Add(rule);
        return new Output(rule.Id);
    }
}


/// <summary>The retire half of the rule registry (docs/34 M5 fix 4): the gate skips retired
/// rules, so retiring UN-GATES the operation — a tenant is never locked into an approval
/// flow it defined. Retire-don't-delete, like every registry artifact.</summary>
[Operation("approvals.rules.retire")]
[Authorize("approvals.manage")]
public static class RetireRule
{
    public sealed record Input([property: LabelKey("approvals.labels.rule")] Guid RuleId);

    public sealed record Output(Guid RuleId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var rule = await tam.Db.Set<ApprovalRule>()
            .SingleOrDefaultAsync(r => r.Id == input.RuleId, ct);
        if (rule is null) return RuleFindings.UnknownRule.At(nameof(Input.RuleId));

        rule.Retired = true;
        return new Output(rule.Id);
    }
}


[View("approvals.rules.list")]
[Authorize("approvals.manage")]
public static class RuleList
{
    public sealed record Query();

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("approvals.labels.operation")]
        public string OperationId { get; init; } = "";
        [LabelKey("approvals.labels.group")]
        public string Group { get; init; } = "";
        [LabelKey("approvals.labels.threshold-field")]
        public string? ThresholdField { get; init; }
        [LabelKey("approvals.labels.threshold")]
        public decimal? Threshold { get; init; }
        public bool Retired { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam) =>
        tam.Db.Set<ApprovalRule>().Select(r => new Result
        {
            Id = r.Id,
            OperationId = r.OperationId,
            Group = tam.Db.Set<ApprovalGroup>()
                .Where(g => g.Id == r.GroupId).Select(g => g.Name).FirstOrDefault() ?? "",
            ThresholdField = r.ThresholdField,
            Threshold = r.Threshold,
            Retired = r.Retired,
        });

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.OperationId))
        .Filterable(nameof(Result.Retired))
        .DefaultSort(nameof(Result.OperationId));
}
