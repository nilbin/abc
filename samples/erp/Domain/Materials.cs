using Tam;

namespace Erp;

// The Materials aggregate: value types, finding factories, entity (docs/02).

public readonly record struct MaterialLineId(Guid Value);


public static class MaterialFindings
{
    public static readonly FindingFactory InvalidQuantity = Finding.Error("materials.invalid-quantity");
    public static readonly FindingFactory StockItemInactive = Finding.Error("materials.stock-item-inactive");
    public static readonly FindingFactory WorkOrderClosed = Finding.Error("materials.work-order-closed");
}


/// <summary>Stock consumption on a work order (docs/34 M3). UnitPrice is a SNAPSHOT of the
/// stock item's price at entry time — catalog price changes never rewrite booked history —
/// and Amount (quantity × snapshot price) is stored with it.</summary>
public sealed class MaterialLine : Tam.EntityFrameworkCore.ITenantScoped
{
    private MaterialLine() { }

    public MaterialLineId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public WorkOrderId WorkOrderId { get; private set; }
    public StockItemId StockItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money Amount { get; private set; }

    public static MaterialLine Add(
        string tenantId, WorkOrderId workOrderId, StockItemId stockItemId,
        decimal quantity, decimal unitPriceSnapshot) => new()
    {
        Id = new MaterialLineId(Guid.NewGuid()),
        TenantId = tenantId,
        WorkOrderId = workOrderId,
        StockItemId = stockItemId,
        Quantity = quantity,
        UnitPrice = unitPriceSnapshot,
        Amount = decimal.Round(quantity * unitPriceSnapshot, 2),
    };
}
