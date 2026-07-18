using Tam;

namespace Erp;

// The Materials aggregate: value types, finding factories, entity (docs/02).

public readonly record struct MaterialLineId(Guid Value);


public static class MaterialFindings
{
    public static readonly FindingFactory InvalidQuantity = Finding.Error("materials.invalid-quantity");
    public static readonly FindingFactory StockItemInactive = Finding.Error("materials.stock-item-inactive");
    public static readonly FindingFactory OrderClosed = Finding.Error("materials.order-closed");
}


/// <summary>Stock consumption on an order (docs/34 M3). UnitPrice is a SNAPSHOT of the
/// stock item's price at entry time — catalog price changes never rewrite booked history —
/// and Amount (quantity × snapshot price) is stored with it.</summary>
public sealed class MaterialLine : Tam.EntityFrameworkCore.ITenantScoped
{
    private MaterialLine() { }

    public MaterialLineId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public OrderId OrderId { get; private set; }
    public StockItemId StockItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money Amount { get; private set; }

    public static MaterialLine Add(
        string tenantId, OrderId orderId, StockItemId stockItemId,
        decimal quantity, decimal unitPriceSnapshot) => new()
    {
        Id = new MaterialLineId(Guid.NewGuid()),
        TenantId = tenantId,
        OrderId = orderId,
        StockItemId = stockItemId,
        Quantity = quantity,
        UnitPrice = unitPriceSnapshot,
        Amount = decimal.Round(quantity * unitPriceSnapshot, 2),
    };
}
