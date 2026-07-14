using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Inspect;

public static class InspectFindings
{
    public static readonly FindingFactory ChecklistIncomplete =
        Finding.Error("inspect.checklist-incomplete");
}

/// <summary>
/// Plugin operations and views are ordinary Tam modules under the plugin's permanent prefix
/// (PLG001). They use <see cref="ITamDb"/> — the framework database seam — never the host's
/// concrete DbContext type: a plugin composes around the host, it does not reach into it.
/// </summary>
[Operation("inspect.checklists.create")]
[Authorize("inspect.checklists.manage")]
public static class CreateChecklist
{
    public sealed record Input(
        [property: LabelKey("inspect.labels.title")] string Title,
        [property: LabelKey("inspect.labels.order")] Guid? OrderId = null);

    public sealed record Output(Guid ChecklistId);

    public static Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var checklist = Checklist.Create(context.TenantId.Value, input.Title, input.OrderId);
        tam.Db.Add(checklist);
        return Task.FromResult<Result<Output>>(new Output(checklist.Id));
    }
}

[Operation("inspect.checklists.pass")]
[Authorize("inspect.checklists.manage")]
public static class PassChecklist
{
    public sealed record Input(
        [property: LabelKey("inspect.labels.checklist-id")] Guid ChecklistId);

    public sealed record Output(Guid ChecklistId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var checklist = await tam.Db.Set<Checklist>().SingleOrDefaultAsync(
            x => x.Id == input.ChecklistId && x.TenantId == context.TenantId.Value, ct);
        if (checklist is null) return PipelineFindings.NotFound.Create();

        checklist.Passed = true;
        return new Result<Output> { Output = new Output(checklist.Id) }
            .Effect(new EventPublished("inspect.checklist-passed", new { checklistId = checklist.Id }));
    }
}

[View("inspect.checklists.list")]
[Authorize("inspect.checklists.read")]
public static class ChecklistList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("inspect.labels.title")]
        public string Title { get; init; } = "";
        [LabelKey("inspect.labels.passed")]
        public bool Passed { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var checklists = tam.Db.Set<Checklist>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
            checklists = checklists.Where(x => x.Title.Contains(query.Search!));
        return checklists.Select(x => new Result { Id = x.Id, Title = x.Title, Passed = x.Passed });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Title))
        .Filterable(nameof(Result.Passed), nameof(Result.Title))
        .DefaultSort(nameof(Result.Title));
}
