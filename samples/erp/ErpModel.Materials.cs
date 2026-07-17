using Erp.Features;
using Tam;

namespace Erp;

public static partial class ErpModel
{
    // No standalone page: the material grid lives as the work-order record's Materials tab,
    // and adding rides the work-orders grid as a row action.
    private static TamModelBuilder AddMaterials(this TamModelBuilder model) => model
        .Form<AddMaterialLine.Input>("web.materials.add", "materials.add", form =>
        {
            form.Field(x => x.WorkOrderId).Renderer("hidden");
            form.Field(x => x.StockItemId);   // options arrive via the stock-items derivation
            form.Field(x => x.Quantity);
        })

        .Grid<MaterialLineList.Result>("web.materials.list", "materials.list");
}
