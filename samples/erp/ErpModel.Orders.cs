using Erp.Features;
using Tam;

namespace Erp;

public static partial class ErpModel
{
    // The orders page is a DECLARED COMPOSITION (docs/32): grid + tabbed record surface.
    private static TamModelBuilder AddOrders(this TamModelBuilder model) => model
        .Page("orders", page => page
            .Grid("web.orders.list")
            .Record(record => record
                .Detail("orders.detail", key: "orderId")
                .Title("number")
                // Record TABS (docs/32 arc 4): the edit form gets its tab, and PanelTabs
                // expands the detail slot into one tab per contributing PLUGIN — the host
                // never names, counts, or labels the plugins (docs/31 D-X4).
                .Tab("details", "erp.tabs.details", s => s.Form("web.orders.edit"))
                // The record's documents (docs/35): the tam.documents grid filtered by THIS
                // order's EntityRef — no dedicated view, no React.
                .Tab("documents", "erp.tabs.documents", s => s
                    .Grid("web.documents.list", bind => bind.QueryEntityRef("attachedTo", "order")))
                .PanelTabs("web.orders.detail")))

        .Form<CreateOrder.Input>("web.orders.create", "orders.create", form =>
        {
            form.Field(x => x.CustomerId);   // [Lookup] on CustomerId renders the picker
            form.Field(x => x.OrderType);
            form.Field(x => x.ProjectId)
                .VisibleWhen(x => x.OrderType == OrderType.Project)
                .RequiredWhen(x => x.OrderType == OrderType.Project)
                // A project belongs to the picked customer — the previous customer's project
                // must not survive a customer change (docs/05 ResetOn).
                .ResetOn(x => x.CustomerId);
            form.Field(x => x.WorkAddress)
                .OnSourceChange(DependentValuePolicy.RecomputeIfUntouched);
            form.Field(x => x.Description);
            form.Field(x => x.RequestedDate);
            form.Field(x => x.EstimatedTotal);
            form.Extensions();
        })

        .Form<EditOrderDetails.Input>("web.orders.edit", "orders.edit-details", form =>
        {
            form.Field(x => x.OrderId).Renderer("hidden");
            form.Field(x => x.Description);
            form.Field(x => x.RequestedDate);
            form.Field(x => x.WorkAddress);
            form.Field(x => x.EstimatedTotal);
            form.Extensions();
        })

        .Grid<OrderList.Result>("web.orders.list", "orders.list", grid =>
        {
            grid.Column(x => x.Number);
            grid.Column(x => x.TenantId);   // the company column — rendered only when acting above a leaf
            grid.Column(x => x.CustomerName);
            grid.Column(x => x.Type);
            grid.Column(x => x.Status);
            grid.Column(x => x.RequestedDate);
            grid.Column(x => x.EstimatedTotal);
            grid.Extensions();
            grid.RowAction("orders.complete");
            grid.RowAction("orders.cancel");
            grid.ToolbarAction("orders.create");
        });
}
