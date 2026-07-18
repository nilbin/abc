using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

/// <summary>Material consumption follows the ORDER, not the person: any technician on
/// site may register what was used (an assisting tech books parts on a colleague's order), so
/// there is no own scope here — the honest boundary is the tenant plus the materials.add atom.
/// The price SNAPSHOT is taken from the stock item at entry time (docs/34 M3): a later
/// catalog price change never rewrites booked history.</summary>
[Operation("materials.add")]
[Authorize("materials.add")]
public static class AddMaterialLine
{
    public sealed record Input(
        [property: LabelKey("labels.order")] OrderId OrderId,
        StockItemId StockItemId,
        decimal Quantity);

    public sealed record Output(MaterialLineId MaterialLineId, Money Amount);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;
        // Materials book onto LIVE work, same window as time.
        if (order.Status is OrderStatus.Completed or OrderStatus.Cancelled)
            return MaterialFindings.OrderClosed.At(nameof(Input.OrderId));

        if (input.Quantity <= 0)
            return MaterialFindings.InvalidQuantity.At(nameof(Input.Quantity));

        var item = await db.Stock.SingleOrDefaultAsync(x => x.Id == input.StockItemId, ct);
        if (item is null) return StockFindings.NotFound.At(nameof(Input.StockItemId));
        // Retired items keep their history but take no NEW consumption (stock.deactivate).
        if (!item.IsActive)
            return MaterialFindings.StockItemInactive.At(nameof(Input.StockItemId));

        var line = MaterialLine.Add(
            context.TenantId.Value, order.Id, item.Id, input.Quantity, item.UnitPrice);
        db.MaterialLines.Add(line);
        return new Output(line.Id, line.Amount);
    }
}

[View("materials.list")]
[Authorize("materials.read")]
public static class MaterialLineList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public MaterialLineId Id { get; init; }
        public OrderNumber OrderNumber { get; init; }
        public Sku Sku { get; init; }
        [LabelKey("labels.stock-item")]
        public string StockItemName { get; init; } = "";
        public StockUnit Unit { get; init; }
        public decimal Quantity { get; init; }
        public Money UnitPrice { get; init; }
        public Money Amount { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        // Materials follow the order (see materials.add): no own scope, no roll-up —
        // every source below rides the ambient tenant filter.
        var rows = db.MaterialLines
            .Join(db.Orders, m => m.OrderId, o => o.Id, (m, o) => new { m, o })
            .Join(db.Stock, x => x.m.StockItemId, s => s.Id, (x, s) => new Result
            {
                Id = x.m.Id, OrderNumber = x.o.Number, Sku = s.Sku, StockItemName = s.Name,
                Unit = s.Unit, Quantity = x.m.Quantity, UnitPrice = x.m.UnitPrice,
                Amount = x.m.Amount,
            });
        if (!string.IsNullOrWhiteSpace(query.Search))
            rows = rows.Where(x =>
                ((string)(object)x.OrderNumber).Contains(query.Search!) ||
                x.StockItemName.Contains(query.Search!) ||
                ((string)(object)x.Sku).Contains(query.Search!));
        return rows;
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.OrderNumber), nameof(Result.StockItemName), nameof(Result.Amount))
        .Filterable(nameof(Result.OrderNumber), nameof(Result.Sku), nameof(Result.StockItemName),
            nameof(Result.Unit))
        .DefaultSort(nameof(Result.OrderNumber), descending: true);
}

/// <summary>Read-only record surface behind the declared materials page: a booked line is
/// history — there is no edit form to prefill, and the snapshot price stays what it was.</summary>
[View("materials.detail")]
[Authorize("materials.read")]
public static class MaterialLineDetail
{
    public sealed record Query(MaterialLineId MaterialLineId);

    public sealed record Result
    {
        public MaterialLineId Id { get; init; }
        public OrderNumber OrderNumber { get; init; }
        public Sku Sku { get; init; }
        [LabelKey("labels.stock-item")]
        public string StockItemName { get; init; } = "";
        public StockUnit Unit { get; init; }
        public decimal Quantity { get; init; }
        public Money UnitPrice { get; init; }
        public Money Amount { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context) =>
        db.MaterialLines.Where(x => x.Id == query.MaterialLineId)
            .Join(db.Orders, m => m.OrderId, o => o.Id, (m, o) => new { m, o })
            .Join(db.Stock, x => x.m.StockItemId, s => s.Id, (x, s) => new Result
            {
                Id = x.m.Id, OrderNumber = x.o.Number, Sku = s.Sku, StockItemName = s.Name,
                Unit = s.Unit, Quantity = x.m.Quantity, UnitPrice = x.m.UnitPrice,
                Amount = x.m.Amount,
            });
}
