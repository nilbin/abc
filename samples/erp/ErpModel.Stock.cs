using Erp.Features;
using Tam;

namespace Erp;

public static partial class ErpModel
{
    private static TamModelBuilder AddStock(this TamModelBuilder model) => model
        .Page("stock", page => page
            .Grid("web.stock.list")
            .Record(record => record
                .Detail("stock.detail", key: "stockItemId")
                .Title("name")
                .Form("web.stock.edit")))

        // No configure: the record IS the form (docs/32 D-P6).
        .Form<CreateStockItem.Input>("web.stock.create", "stock.create")

        .Form<EditStockItem.Input>("web.stock.edit", "stock.edit", form =>
        {
            form.Field(x => x.StockItemId).Renderer("hidden");
            form.Field(x => x.Name);
            form.Field(x => x.UnitPrice);
        })

        .Grid<StockList.Result>("web.stock.list", "stock.list", grid =>
        {
            grid.Column(x => x.Sku);
            grid.Column(x => x.Name);
            grid.Column(x => x.Unit);
            grid.Column(x => x.UnitPrice);
            grid.Column(x => x.IsActive);
            grid.RowAction("stock.deactivate");
            grid.ToolbarAction("stock.create");
        });
}
