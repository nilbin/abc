using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests.Framework;

public static class WidgetFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("widgets.not-found");
    public static readonly FindingFactory BinRequired = Finding.Error("widgets.bin-required");
    public static readonly FindingFactory BinNotAvailable = Finding.Error("widgets.bin-not-available");
}

/// <summary>The extensible create (the "order" analog). Publishes widget-created; a Special widget must
/// carry a Bin drawn from its group's candidate universe (the membership analog of a project order).</summary>
[Operation("widgets.create")]
[Authorize("widgets.create")]
[AcceptsExtensions(typeof(Widget))]
public static class CreateWidget
{
    public sealed record Input(
        string Name,
        string Description = "",
        WidgetLocation Location = default,
        WidgetCategory Category = WidgetCategory.Standard,
        Guid GroupId = default,
        BinId? BinId = null);

    public sealed record Output(Guid WidgetId);

    public static Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            Name = input.Name,
            Description = input.Description,
            Location = input.Location.Value ?? "",
            Category = input.Category,
            BinId = input.BinId,
        };
        db.Widgets.Add(widget);
        return Task.FromResult<Result<Output>>(new Result<Output> { Output = new Output(widget.Id) }
            .Effect(new EventPublished(new WidgetCreated(widget.Id, widget.Name, widget.Category))));
    }
}

/// <summary>The extensible edit (merge + Change&lt;T&gt; derivations).</summary>
[Operation("widgets.edit")]
[Authorize("widgets.edit")]
[AcceptsExtensions(typeof(Widget))]
public static class EditWidget
{
    public sealed record Input(
        Guid WidgetId,
        Change<string>? Name = null,
        Change<string>? Description = null);

    public sealed record Output(Guid WidgetId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(x => x.Id == input.WidgetId, ct);
        if (widget is null) return WidgetFindings.NotFound.Create();
        var merge = TamMerge.Apply(widget, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();
        return new Output(widget.Id);
    }
}

/// <summary>A NON-extensible create — the vehicle for "an extensions channel on an operation that does
/// not accept extensions is rejected". Still writes a Widget so a test can prove the handler never ran.</summary>
[Operation("widgets.create-plain")]
[Authorize("widgets.create")]
public static class CreateWidgetPlain
{
    public sealed record Input(string Name);
    public sealed record Output(Guid WidgetId);

    public static Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var widget = new Widget { Id = Guid.NewGuid(), TenantId = context.TenantId.Value, Name = input.Name };
        db.Widgets.Add(widget);
        return Task.FromResult<Result<Output>>(new Output(widget.Id));
    }
}

/// <summary>An edit INTENT carrying a rule-condition input field (priority) — the RuleAction trigger.</summary>
[Operation("widgets.set-priority")]
[Authorize("widgets.edit")]
public static class SetWidgetPriority
{
    public sealed record Input(Guid WidgetId, WidgetPriority Priority);
    public sealed record Output(Guid WidgetId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(x => x.Id == input.WidgetId, ct);
        if (widget is null) return WidgetFindings.NotFound.Create();
        widget.Priority = input.Priority;
        return new Output(widget.Id);
    }
}

/// <summary>A lifecycle intent — a valid finding-rule trigger (the RuleDefinition tests use it).</summary>
[Operation("widgets.complete")]
[Authorize("widgets.edit")]
public static class CompleteWidget
{
    public sealed record Input(Guid WidgetId);
    public sealed record Output(Guid WidgetId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(x => x.Id == input.WidgetId, ct);
        if (widget is null) return WidgetFindings.NotFound.Create();
        return new Output(widget.Id);
    }
}

/// <summary>Closing a Bin — the rules.schema trigger whose single BinId target row is the Bin entity,
/// so rules.schema offers row.status / row.budget (but never row.extensions).</summary>
[Operation("bins.close")]
[Authorize("bins.manage")]
public static class CloseBin
{
    public sealed record Input(BinId BinId);
    public sealed record Output(BinId BinId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var bin = await db.Bins.SingleOrDefaultAsync(x => x.Id == input.BinId, ct);
        if (bin is null) return WidgetFindings.NotFound.Create();
        bin.Status = BinStatus.Closed;
        return new Output(input.BinId);
    }
}

/// <summary>Creating a Bin — publishes bin-created (an EventRule trigger whose payload references the Bin
/// row a set-field action writes).</summary>
[Operation("bins.create")]
[Authorize("bins.manage")]
[AcceptsExtensions(typeof(Bin))]
public static class CreateBin
{
    public sealed record Input(Guid GroupId, string Name, WidgetCategory Category = WidgetCategory.Standard, decimal? Budget = null);
    public sealed record Output(BinId BinId);

    public static Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var bin = new Bin
        {
            Id = new BinId(Guid.NewGuid()),
            TenantId = context.TenantId.Value,
            GroupId = input.GroupId,
            Name = input.Name,
            Category = input.Category,
            Status = BinStatus.Open,
            Budget = input.Budget,
        };
        db.Bins.Add(bin);
        return Task.FromResult<Result<Output>>(new Result<Output> { Output = new Output(bin.Id) }
            .Effect(new EventPublished(new BinCreated(bin.Id, bin.Name, bin.Category))));
    }
}

/// <summary>Setting a Bin's status — a RuleAction trigger carrying a rule-condition input field (status),
/// whose target row is the Bin (a set-field action can flag its extension).</summary>
[Operation("bins.set-status")]
[Authorize("bins.manage")]
[AcceptsExtensions(typeof(Bin))]
public static class SetBinStatus
{
    public sealed record Input(BinId BinId, BinStatus Status);
    public sealed record Output(BinId BinId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var bin = await db.Bins.SingleOrDefaultAsync(x => x.Id == input.BinId, ct);
        if (bin is null) return WidgetFindings.NotFound.Create();
        bin.Status = input.Status;
        return new Output(input.BinId);
    }
}

/// <summary>The Bin picker + membership universe: open bins, scopable to one Group (the filterable
/// GroupId) — one mechanism serves both the picker and the authoritative membership check.</summary>
[View("bins.lookup")]
[Authorize("bins.read")]
public static class BinLookup
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public BinId Id { get; init; }
        public string Name { get; init; } = "";
        public Guid GroupId { get; init; }
        public BinStatus Status { get; init; }
        public decimal? Budget { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, WidgetDbContext db, OperationContext context)
    {
        var bins = db.Bins.Where(x => x.Status == BinStatus.Open);
        if (!string.IsNullOrWhiteSpace(query.Search))
            bins = bins.Where(x => x.Name.Contains(query.Search!));
        return bins.Select(x => new Result
        {
            Id = x.Id, Name = x.Name, GroupId = x.GroupId, Status = x.Status, Budget = x.Budget,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name))
            .Filterable(nameof(Result.GroupId));
}
