using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

[Operation("stock.create")]
[Authorize("stock.manage")]
public static class CreateStockItem
{
    public sealed record Input(
        Sku Sku,
        string Name,
        StockUnit Unit,
        Money UnitPrice);

    public sealed record Output(StockItemId StockItemId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var exists = await db.Stock.AnyAsync(x => x.Sku == input.Sku, ct);
        if (exists)
            return StockFindings.DuplicateSku.At(nameof(Input.Sku));

        var item = StockItem.Create(
            context.TenantId.Value, input.Sku, input.Name, input.Unit, input.UnitPrice);
        db.Stock.Add(item);
        return new Output(item.Id);
    }
}

[Operation("stock.edit")]
[Authorize("stock.manage")]
public static class EditStockItem
{
    public sealed record Input(
        [property: LabelKey("labels.stock-item")] StockItemId StockItemId,
        Change<string>? Name = null,
        Change<Money>? UnitPrice = null);

    public sealed record Output(StockItemId StockItemId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var item = await db.Stock.SingleOrDefaultAsync(x => x.Id == input.StockItemId, ct);
        if (item is null) return StockFindings.NotFound.Create();

        var merge = TamMerge.Apply(item, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();

        return new Output(item.Id);
    }
}

/// <summary>Deactivation is an INTENT (EDIT001) and a retirement, not a deletion — material
/// lines will keep referencing the item (docs/34 M3).</summary>
[Operation("stock.deactivate")]
[Authorize("stock.manage")]
public static class DeactivateStockItem
{
    public sealed record Input([property: LabelKey("labels.stock-item")] StockItemId StockItemId);

    public sealed record Output(bool IsActive);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var item = await db.Stock.SingleOrDefaultAsync(x => x.Id == input.StockItemId, ct);
        if (item is null) return StockFindings.NotFound.Create();

        item.Deactivate();
        return new Output(item.IsActive);
    }
}

[View("stock.list")]
[Authorize("stock.read")]
public static class StockList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public StockItemId Id { get; init; }
        public Sku Sku { get; init; }
        public string Name { get; init; } = "";
        public StockUnit Unit { get; init; }
        public Money UnitPrice { get; init; }
        public bool IsActive { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        // The stock catalog is per-node (no subtree roll-up, no inherited sharing): each
        // company prices its own materials. The ambient filter is the whole scope story.
        var items = db.Stock.AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
            items = items.Where(x =>
                x.Name.Contains(query.Search!) ||
                ((string)(object)x.Sku).Contains(query.Search!));

        return items.Select(x => new Result
        {
            Id = x.Id, Sku = x.Sku, Name = x.Name, Unit = x.Unit,
            UnitPrice = x.UnitPrice, IsActive = x.IsActive,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Sku), nameof(Result.Name), nameof(Result.UnitPrice))
        .Filterable(nameof(Result.Unit), nameof(Result.IsActive), nameof(Result.UnitPrice))
        .DefaultSort(nameof(Result.Sku));
}

[View("stock.detail")]
[Authorize("stock.read")]
public static class StockDetail
{
    public sealed record Query(StockItemId StockItemId);

    public sealed record Result
    {
        public StockItemId Id { get; init; }
        public Sku Sku { get; init; }
        public string Name { get; init; } = "";
        public StockUnit Unit { get; init; }
        public Money UnitPrice { get; init; }
        public bool IsActive { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context) =>
        db.Stock.Where(x => x.Id == query.StockItemId)
            .Select(x => new Result
            {
                Id = x.Id, Sku = x.Sku, Name = x.Name, Unit = x.Unit,
                UnitPrice = x.UnitPrice, IsActive = x.IsActive,
            });
}
