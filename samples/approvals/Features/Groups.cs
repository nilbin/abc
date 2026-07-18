using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Approvals;

public static class GroupFindings
{
    public static readonly FindingFactory UnknownGroup = Finding.Error("approvals.unknown-group");
    public static readonly FindingFactory UnknownMember = Finding.Error("approvals.unknown-member");
}


[Operation("approvals.groups.define")]
[Authorize("approvals.manage")]
public static class DefineGroup
{
    public sealed record Input(
        [property: LabelKey("approvals.labels.name")] string Name,
        [property: LabelKey("approvals.labels.parent-group")] Guid? ParentGroupId = null);

    public sealed record Output(Guid GroupId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        if (input.ParentGroupId is { } parent
            && !await tam.Db.Set<ApprovalGroup>().AnyAsync(g => g.Id == parent, ct))
            return GroupFindings.UnknownGroup.At(nameof(Input.ParentGroupId));

        var group = new ApprovalGroup
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            Name = input.Name,
            ParentGroupId = input.ParentGroupId,
        };
        tam.Db.Add(group);
        return new Output(group.Id);
    }
}


[Operation("approvals.groups.assign")]
[Authorize("approvals.manage")]
public static class AssignMember
{
    public sealed record Input(
        [property: LabelKey("approvals.labels.group")] Guid GroupId,
        [property: LabelKey("approvals.labels.email")] string Email);

    public sealed record Output(Guid GroupId, string ActorId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam,
        Tam.AspNetCore.ITamDirectory directory, CancellationToken ct)
    {
        if (!await tam.Db.Set<ApprovalGroup>().AnyAsync(g => g.Id == input.GroupId, ct))
            return GroupFindings.UnknownGroup.At(nameof(Input.GroupId));

        // The directory seam resolves only accounts that can already ACT in this tenant
        // (docs/26: the tenant boundary is the membership) — the plugin never touches
        // identity tables directly.
        var actorId = await directory.ActorIdByEmailAsync(input.Email, ct);
        if (actorId is null)
            return GroupFindings.UnknownMember.At(nameof(Input.Email));

        if (!await tam.Db.Set<ApprovalGroupMember>()
                .AnyAsync(m => m.GroupId == input.GroupId && m.ActorId == actorId, ct))
            tam.Db.Add(new ApprovalGroupMember
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId.Value,
                GroupId = input.GroupId,
                ActorId = actorId,
            });
        return new Output(input.GroupId, actorId);
    }
}


[View("approvals.groups.list")]
[Authorize("approvals.manage")]
public static class GroupList
{
    public sealed record Query();

    public sealed record Result
    {
        public Guid Id { get; init; }
        // The assign form's field name, carried deliberately: the grid's RowForm prefills
        // same-named fields, so a row opens the form with its group already bound (docs/32).
        public Guid GroupId { get; init; }
        [LabelKey("approvals.labels.name")]
        public string Name { get; init; } = "";
        [LabelKey("approvals.labels.parent-group")]
        public Guid? ParentGroupId { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam) =>
        tam.Db.Set<ApprovalGroup>().Select(g => new Result
        {
            Id = g.Id, GroupId = g.Id, Name = g.Name, ParentGroupId = g.ParentGroupId,
        });

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Name))
        .DefaultSort(nameof(Result.Name));
}
