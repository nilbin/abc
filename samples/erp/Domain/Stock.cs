using Tam;

namespace Erp;

// The Stock aggregate: value types, finding factories, entity (docs/02).


[LabelKey("labels.stock-item"), Lookup("stock.lookup")]
public readonly record struct StockItemId(Guid Value);


[LabelKey("labels.sku")]
public readonly record struct Sku(string Value);


public enum StockUnit { Piece, Hour, Meter, Kilogram, Litre }


public static class StockFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("stock.not-found");
    public static readonly FindingFactory DuplicateSku = Finding.Error("stock.duplicate-sku");
}


public sealed class StockItem : Tam.EntityFrameworkCore.ITenantScoped
{
    private StockItem() { }

    public StockItemId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Sku Sku { get; private set; }
    public string Name { get; private set; } = "";
    public StockUnit Unit { get; private set; }
    public Money UnitPrice { get; private set; }
    public bool IsActive { get; private set; }

    public static StockItem Create(
        string tenantId, Sku sku, string name, StockUnit unit, decimal unitPrice) => new()
    {
        Id = new StockItemId(Guid.NewGuid()),
        TenantId = tenantId,
        Sku = sku,
        Name = name,
        Unit = unit,
        UnitPrice = unitPrice,
        IsActive = true,
    };

    public void Deactivate() => IsActive = false;
}
