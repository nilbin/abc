using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Inspect;

public static class ChecklistFindings
{
    public static readonly FindingFactory ChecklistIncomplete =
        Finding.Error("inspect.checklist-incomplete");
    public static readonly FindingFactory ItemsOpen =
        Finding.Error("inspect.items-open");
}

// ---------------------------------------------------------------------------------------
// Checklists: manual creation (v1, kept), per-item check-off intents (v2), and the pass
// intent. Consequential state transitions are intents, never partial edits (EDIT001).
// ---------------------------------------------------------------------------------------

[Operation("inspect.checklists.create")]
[Authorize("inspect.checklists.manage")]
public static class CreateChecklist
{
    public sealed record Input(
        [property: LabelKey("inspect.labels.title")] string Title,
        [property: LabelKey("inspect.labels.order")] Guid? OrderId = null,
        [property: LabelKey("inspect.labels.mandatory")] bool Mandatory = false);

    public sealed record Output(Guid ChecklistId);

    public static Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var checklist = Checklist.Create(
            context.TenantId.Value, input.Title, input.OrderId, input.Mandatory);
        tam.Db.Add(checklist);
        return Task.FromResult<Result<Output>>(new Output(checklist.Id));
    }
}

/// <summary>Passing is the checklist-level close: refused while line items are open, so the
/// item state and the checklist state can never disagree. Item-less (manual) checklists
/// pass directly — this is their only completion path.</summary>
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
            x => x.Id == input.ChecklistId, ct);
        if (checklist is null) return PipelineFindings.NotFound.Create();

        var open = await tam.Db.Set<ChecklistItem>().CountAsync(
            x => x.ChecklistId == checklist.Id && !x.Done, ct);
        if (open > 0)
            return ChecklistFindings.ItemsOpen.With(("open", open));

        checklist.Pass();
        return new Result<Output> { Output = new Output(checklist.Id) }
            .Effect(new EventPublished("inspect.checklist-passed", new { checklistId = checklist.Id }));
    }
}

/// <summary>Check one item off. When the last open item closes, the checklist passes as a
/// consequence — one intent, one consistent state, no separate ceremony (EDIT001: the
/// transition is the operation's job, not a client-side field edit).</summary>
[Operation("inspect.items.check")]
[Authorize("inspect.checklists.manage")]
public static class CheckItem
{
    public sealed record Input(
        [property: LabelKey("inspect.labels.item")] Guid ItemId);

    public sealed record Output(Guid ChecklistId, bool ChecklistPassed);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var item = await tam.Db.Set<ChecklistItem>().SingleOrDefaultAsync(
            x => x.Id == input.ItemId, ct);
        if (item is null) return PipelineFindings.NotFound.Create();

        item.Check();

        var stillOpen = await tam.Db.Set<ChecklistItem>().AnyAsync(
            x => x.ChecklistId == item.ChecklistId && x.Id != item.Id && !x.Done, ct);
        if (stillOpen) return new Output(item.ChecklistId, ChecklistPassed: false);

        var checklist = await tam.Db.Set<Checklist>().SingleOrDefaultAsync(
            x => x.Id == item.ChecklistId, ct);
        if (checklist is null) return new Output(item.ChecklistId, ChecklistPassed: false);

        checklist.Pass();
        return new Result<Output> { Output = new Output(checklist.Id, ChecklistPassed: true) }
            .Effect(new EventPublished("inspect.checklist-passed", new { checklistId = checklist.Id }));
    }
}

/// <summary>The correction path: un-check re-opens the item AND the checklist — the paired
/// intent keeps both reachable states honest instead of leaving a passed checklist with an
/// open item (why this exists rather than a pass-only model: inspections get amended).</summary>
[Operation("inspect.items.uncheck")]
[Authorize("inspect.checklists.manage")]
public static class UncheckItem
{
    public sealed record Input(
        [property: LabelKey("inspect.labels.item")] Guid ItemId);

    public sealed record Output(Guid ChecklistId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var item = await tam.Db.Set<ChecklistItem>().SingleOrDefaultAsync(
            x => x.Id == input.ItemId, ct);
        if (item is null) return PipelineFindings.NotFound.Create();

        item.Uncheck();
        var checklist = await tam.Db.Set<Checklist>().SingleOrDefaultAsync(
            x => x.Id == item.ChecklistId, ct);
        checklist?.Reopen();
        return new Output(item.ChecklistId);
    }
}

[View("inspect.checklists.list")]
[Authorize("inspect.checklists.read")]
public static class ChecklistList
{
    public sealed record Query(string? Search = null, Guid? OrderId = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("inspect.labels.title")]
        public string Title { get; init; } = "";
        [LabelKey("inspect.labels.mandatory")]
        public bool Mandatory { get; init; }
        [LabelKey("inspect.labels.open-items")]
        public int OpenItems { get; init; }
        [LabelKey("inspect.labels.passed")]
        public bool Passed { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var checklists = tam.Db.Set<Checklist>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
            checklists = checklists.Where(x => x.Title.Contains(query.Search!));
        if (query.OrderId is { } orderId)
            checklists = checklists.Where(x => x.OrderId == orderId);
        return checklists.Select(x => new Result
        {
            Id = x.Id,
            Title = x.Title,
            Mandatory = x.Mandatory,
            OpenItems = tam.Db.Set<ChecklistItem>().Count(i => i.ChecklistId == x.Id && !i.Done),
            Passed = x.Passed,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Title))
        .Filterable(nameof(Result.Passed), nameof(Result.Title), nameof(Result.Mandatory))
        .DefaultSort(nameof(Result.Title));
}

/// <summary>Checklist items keyed by the ORDER — the order-detail panel's grid (bound to the
/// slot's record context) and the check/uncheck row actions live here.</summary>
[View("inspect.items.list")]
[Authorize("inspect.checklists.read")]
public static class ChecklistItemList
{
    public sealed record Query(Guid? OrderId = null, Guid? ChecklistId = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("inspect.labels.title")]
        public string ChecklistTitle { get; init; } = "";
        [LabelKey("inspect.labels.position")]
        public int Position { get; init; }
        [LabelKey("inspect.labels.item-text")]
        public string Text { get; init; } = "";
        [LabelKey("inspect.labels.done")]
        public bool Done { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var items = tam.Db.Set<ChecklistItem>().AsQueryable();
        if (query.OrderId is { } orderId)
            items = items.Where(x => x.OrderId == orderId);
        if (query.ChecklistId is { } checklistId)
            items = items.Where(x => x.ChecklistId == checklistId);
        return items.Join(tam.Db.Set<Checklist>(),
            i => i.ChecklistId, c => c.Id, (i, c) => new Result
        {
            Id = i.Id,
            ChecklistTitle = c.Title,
            Position = i.Position,
            Text = i.Text,
            Done = i.Done,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Position), nameof(Result.ChecklistTitle))
        .Filterable(nameof(Result.Done), nameof(Result.ChecklistTitle))
        .DefaultSort(nameof(Result.Position));
}
