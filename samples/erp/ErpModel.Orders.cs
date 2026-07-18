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
                // The merged execution side (one entity, one lifecycle): time and materials
                // are the order's own children — bound grids off the record's number.
                .Tab("time", "erp.tabs.time", s => s
                    .Grid("web.time.list", bind => bind.Query("orderNumber", fromRecord: "number")))
                .Tab("materials", "erp.tabs.materials", s => s
                    .Grid("web.materials.list", bind => bind.Query("orderNumber", fromRecord: "number")))
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
            form.Field(x => x.Priority);
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

        .Form<ScheduleOrder.Input>("web.orders.schedule", "orders.schedule", form =>
        {
            form.Field(x => x.OrderId).Renderer("hidden");
            form.Field(x => x.ScheduledDate);
            form.Field(x => x.AssigneeActorId);   // [Lookup("users.lookup")] renders the picker
        })

        // Priority is an enum, so it moves through its own intent (EDIT001), not the
        // change-set — this form is the intent's surface, opened from the grid row.
        .Form<SetOrderPriority.Input>("web.orders.set-priority", "orders.set-priority", form =>
        {
            form.Field(x => x.OrderId).Renderer("hidden");
            form.Field(x => x.Priority);
        })

        .Grid<OrderList.Result>("web.orders.list", "orders.list", grid =>
        {
            grid.Column(x => x.Number);
            grid.Column(x => x.TenantId);   // the company column — rendered only when acting above a leaf
            grid.Column(x => x.CustomerName);
            grid.Column(x => x.Type);
            grid.Column(x => x.Status);
            grid.Column(x => x.Priority);
            grid.Column(x => x.ScheduledDate);
            grid.Column(x => x.AssignedToName);
            grid.Column(x => x.EstimatedTotal);
            grid.Extensions();
            grid.RowAction("orders.schedule");
            grid.RowAction("orders.set-priority");
            grid.RowAction("orders.start");
            grid.RowAction("orders.complete");
            grid.RowAction("orders.cancel");
            // Time and materials are booked FROM the order — the row prefills OrderId.
            grid.RowAction("time.book");
            grid.RowAction("materials.add");
            grid.ToolbarAction("orders.create");
        });
}
