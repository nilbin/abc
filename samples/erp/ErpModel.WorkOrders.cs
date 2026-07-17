using Erp.Features;
using Tam;

namespace Erp;

public static partial class ErpModel
{
    // The record-tabs exemplar (docs/32 arc 4): a work order's Details form, then its child
    // Time and Materials listings — bound grids filtering time.list / materials.list off the
    // record's own number, mechanically, with no dedicated child view.
    private static TamModelBuilder AddWorkOrders(this TamModelBuilder model) => model
        .Page("work-orders", page => page
            .Grid("web.work-orders.list")
            .Record(record => record
                .Detail("work-orders.detail", key: "workOrderId")
                .Title("number")
                .Tab("details", "erp.tabs.details", s => s.Form("web.work-orders.edit"))
                .Tab("time", "erp.tabs.time", s => s
                    .Grid("web.time.list", bind => bind.Query("workOrderNumber", fromRecord: "number")))
                .Tab("materials", "erp.tabs.materials", s => s
                    .Grid("web.materials.list", bind => bind.Query("workOrderNumber", fromRecord: "number")))))

        .Form<CreateWorkOrder.Input>("web.work-orders.create", "work-orders.create", form =>
        {
            form.Field(x => x.ProjectId);
            form.Field(x => x.Title);
            form.Field(x => x.Description);
            form.Field(x => x.Location);
            form.Field(x => x.Priority);
            form.Extensions();
        })

        .Form<EditWorkOrderDetails.Input>("web.work-orders.edit", "work-orders.edit-details", form =>
        {
            form.Field(x => x.WorkOrderId).Renderer("hidden");
            form.Field(x => x.Title);
            form.Field(x => x.Description);
            form.Field(x => x.Location);
            form.Extensions();
        })

        // Priority is an enum, so it moves through its own intent (EDIT001), not the
        // change-set — this form is the intent's surface, opened from the grid row.
        .Form<SetWorkOrderPriority.Input>("web.work-orders.set-priority", "work-orders.set-priority", form =>
        {
            form.Field(x => x.WorkOrderId).Renderer("hidden");
            form.Field(x => x.Priority);
        })

        .Form<ScheduleWorkOrder.Input>("web.work-orders.schedule", "work-orders.schedule", form =>
        {
            form.Field(x => x.WorkOrderId).Renderer("hidden");
            form.Field(x => x.ScheduledDate);
            form.Field(x => x.AssigneeActorId);   // options arrive via the assignees derivation
        })

        .Grid<WorkOrderList.Result>("web.work-orders.list", "work-orders.list", grid =>
        {
            grid.Column(x => x.Number);
            grid.Column(x => x.TenantId);   // the company column — rendered only above a leaf
            grid.Column(x => x.ProjectNumber);
            grid.Column(x => x.Title);
            grid.Column(x => x.Status);
            grid.Column(x => x.Priority);
            grid.Column(x => x.ScheduledDate);
            grid.Column(x => x.AssignedToName);
            grid.Extensions();
            grid.RowAction("work-orders.schedule");
            grid.RowAction("work-orders.set-priority");
            grid.RowAction("work-orders.start");
            grid.RowAction("work-orders.complete");
            grid.RowAction("work-orders.close");
            // Time and materials are booked FROM the work order — the row prefills WorkOrderId.
            grid.RowAction("time.book");
            grid.RowAction("materials.add");
            grid.ToolbarAction("work-orders.create");
        });
}
