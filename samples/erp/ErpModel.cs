using Erp.Features;
using Tam;
using Tam.AspNetCore.SystemOps;
using Tam.Generated;

namespace Erp;

/// <summary>
/// The compiled model is a VALUE (docs/01): built once here, then hosted by Program.cs and
/// tested by Erp.Tests through the SAME instance shape — what the test host verifies is what
/// the web host serves (tutorial Step 11).
/// </summary>
public static class ErpModel
{
    public static TamModel Build() => new TamModelBuilder()
        .DefaultCulture("sv")
        .Locales(Path.Combine(AppContext.BaseDirectory, "locales"))
        .AddDiscovered()   // compile-time discovery from Tam.Compiler — no runtime assembly scan
        .AddTamSystem()    // framework operations/views: custom fields, roles, audit, plugins
        .AddPlugin<Inspect.InspectionPlugin>()   // compiled in; each tenant activates at runtime (docs/22)
        .AddPlugin<Fortnox.FortnoxPlugin>()      // a plugin that ships an inbound integration (docs/10 + docs/22)
        .AddPlugin<Approvals.ApprovalsPlugin>()  // Step 16: approval flows over the three seams (docs/28 D-AG4)
        .AddPlugin<Invoicing.InvoicingPlugin>()  // Step 17: extends the Orders domain (docs/31)

        // The web nav tree (docs/30): the HOST owns layout — modes at the top, the administration
        // section collects every package/plugin page that SUGGESTS it; anything uncollected lands
        // under "more" in the last mode automatically (nothing can be authored into invisibility).
        // Event contracts (docs/31 D-X5): what subscribers/triggers may bind to, with payload shape.
        .PublishesEvent("order-completed", "orderId", "number")
        .PublishesEvent("work-order-completed", "workOrderId", "number")

        // The order detail is a CONTRIBUTION POINT (docs/31 D-X4): placing it on the record
        // below DECLARES it (docs/34 M5 — placement is declaration; the record's key is its
        // context). model.Slot() would only be needed for external slots or a custom key.

        // The orders page is a DECLARED COMPOSITION (docs/32): grid + record surface (detail →
        // edit form → plugin panels). The hand-written OrdersPage React component is gone.
        .Page("orders", page => page
            .Grid("web.orders.list")
            .Record(record => record
                .Detail("orders.detail", key: "orderId")
                .Title("number")
                .Form("web.orders.edit")           // declaration order is layout order (docs/32)
                .Slot("web.orders.detail")))

        // The second declared page — the shape generalizes: grid + record (no slots here; the
        // customers surface is not a contribution point until a plugin needs it).
        .Page("customers", page => page
            .Grid("web.customers.list")
            .Record(record => record
                .Detail("customers.detail", key: "customerId")
                .Title("name")
                .Form("web.customers.edit")))

        // The field-service slice (docs/34 M1): two more declared pages, zero React.
        .Page("projects", page => page
            .Grid("web.projects.list")
            .Record(record => record
                .Detail("projects.detail", key: "projectId")
                .Title("number")
                .Form("web.projects.edit")))

        .Page("stock", page => page
            .Grid("web.stock.list")
            .Record(record => record
                .Detail("stock.detail", key: "stockItemId")
                .Title("name")
                .Form("web.stock.edit")))

        .Page("work-orders", page => page
            .Grid("web.work-orders.list")
            .Record(record => record
                .Detail("work-orders.detail", key: "workOrderId")
                .Title("number")
                .Form("web.work-orders.edit")))

        // Time + materials (docs/34 M3): read-only record surfaces — bookings are history; the
        // only state change is time.approve, an intent riding the grid as a row action.
        .Page("time", page => page
            .Grid("web.time.list")
            .Record(record => record
                .Detail("time.detail", key: "timeEntryId")
                .Title("workOrderNumber")))

        .Page("materials", page => page
            .Grid("web.materials.list")
            .Record(record => record
                .Detail("materials.detail", key: "materialLineId")
                .Title("workOrderNumber")))

        .Nav("web", nav => nav
            .Mode("work", m => m
                .Page("orders", page: "orders", order: 10)   // declared page: permission derives
                .Page("work-orders", page: "work-orders", order: 15)
                .Page("time", page: "time", order: 16)
                .Page("materials", page: "materials", order: 17)
                .Page("projects", page: "projects", order: 20)
                .Page("customers", page: "customers", order: 30)
                .Page("stock", page: "stock", order: 40))
            // The technician's mode (docs/34 M4): the same declared pages, curated for the
            // field — and the thing a tenant can HIDE via the nav override registry (docs/30 v2).
            .Mode("field", m => m
                .Page("my-work", page: "work-orders", order: 10)
                .Page("my-time", page: "time", order: 20))
            .Mode("admin", m => m
                .Section("administration")))

        .Form<CreateOrder.Input>("web.orders.create", "orders.create", form =>
        {
            form.Field(x => x.CustomerId).Renderer("customer-picker");
            form.Field(x => x.OrderType);
            form.Field(x => x.ProjectId)
                .VisibleWhen(x => x.OrderType == OrderType.Project)
                .RequiredWhen(x => x.OrderType == OrderType.Project);
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

        // No configure: the record IS the form — every input field, declaration order (docs/32).
        .Form<CreateCustomer.Input>("web.customers.create", "customers.create")

        // Enumerated only to hide the record key (the modal already IS the customer).
        .Form<EditCustomerContact.Input>("web.customers.edit", "customers.edit-contact", form =>
        {
            form.Field(x => x.CustomerId).Renderer("hidden");
            form.Field(x => x.Name);
            form.Field(x => x.VisitAddress);
            form.Field(x => x.Email);
            form.Field(x => x.Phone);
        })

        .Form<CreateProject.Input>("web.projects.create", "projects.create", form =>
        {
            form.Field(x => x.CustomerId).Renderer("customer-picker");
            form.Field(x => x.Number);
            form.Field(x => x.Name);
            form.Field(x => x.Budget);
        })

        .Form<EditProjectDetails.Input>("web.projects.edit", "projects.edit-details", form =>
        {
            form.Field(x => x.ProjectId).Renderer("hidden");
            form.Field(x => x.Name);
            form.Field(x => x.Budget);
        })

        .Form<CreateWorkOrder.Input>("web.work-orders.create", "work-orders.create", form =>
        {
            form.Field(x => x.ProjectId);
            form.Field(x => x.Title);
            form.Field(x => x.Description);
            form.Field(x => x.Location);
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

        .Form<ScheduleWorkOrder.Input>("web.work-orders.schedule", "work-orders.schedule", form =>
        {
            form.Field(x => x.WorkOrderId).Renderer("hidden");
            form.Field(x => x.ScheduledDate);
            form.Field(x => x.AssigneeActorId);   // options arrive via the assignees derivation
        })

        // Booking rides the work-orders grid as a row action, so the work order arrives prefilled
        // (hidden here); rate and amount are filled live by the time.book derivations —
        // RecomputeIfUntouched keeps them tracking until the user overrides the rate.
        .Form<BookTime.Input>("web.time.book", "time.book", form =>
        {
            form.Field(x => x.WorkOrderId).Renderer("hidden");
            form.Field(x => x.Date);
            form.Field(x => x.Hours);
            form.Field(x => x.HourlyRate)
                .OnSourceChange(DependentValuePolicy.RecomputeIfUntouched);
            form.Field(x => x.Amount)
                .OnSourceChange(DependentValuePolicy.RecomputeIfUntouched);
            form.Field(x => x.Note);
        })

        .Form<AddMaterialLine.Input>("web.materials.add", "materials.add", form =>
        {
            form.Field(x => x.WorkOrderId).Renderer("hidden");
            form.Field(x => x.StockItemId);   // options arrive via the stock-items derivation
            form.Field(x => x.Quantity);
        })

        // No configure: the record IS the form (docs/32 D-P6).
        .Form<CreateStockItem.Input>("web.stock.create", "stock.create")

        .Form<EditStockItem.Input>("web.stock.edit", "stock.edit", form =>
        {
            form.Field(x => x.StockItemId).Renderer("hidden");
            form.Field(x => x.Name);
            form.Field(x => x.UnitPrice);
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
            grid.ToolbarAction("orders.create");
        })


        .Grid<CustomerList.Result>("web.customers.list", "customers.list", grid =>
        {
            grid.Column(x => x.Name);
            grid.Column(x => x.Email);
            grid.Column(x => x.Phone);
            grid.Column(x => x.VisitAddress);
            grid.Column(x => x.IsActive);
            grid.ToolbarAction("customers.create");
        })

        .Grid<ProjectList.Result>("web.projects.list", "projects.list", grid =>
        {
            grid.Column(x => x.Number);
            grid.Column(x => x.TenantId);   // the company column — rendered only above a leaf
            grid.Column(x => x.Name);
            grid.Column(x => x.CustomerName);
            grid.Column(x => x.Status);
            grid.Column(x => x.Budget);
            grid.RowAction("projects.close");
            grid.RowAction("projects.reopen");
            grid.ToolbarAction("projects.create");
        })

        .Grid<WorkOrderList.Result>("web.work-orders.list", "work-orders.list", grid =>
        {
            grid.Column(x => x.Number);
            grid.Column(x => x.TenantId);   // the company column — rendered only above a leaf
            grid.Column(x => x.ProjectNumber);
            grid.Column(x => x.Title);
            grid.Column(x => x.Status);
            grid.Column(x => x.ScheduledDate);
            grid.Column(x => x.AssignedToName);
            grid.Extensions();
            grid.RowAction("work-orders.schedule");
            grid.RowAction("work-orders.start");
            grid.RowAction("work-orders.complete");
            grid.RowAction("work-orders.close");
            // Time and materials are booked FROM the work order — the row prefills WorkOrderId.
            grid.RowAction("time.book");
            grid.RowAction("materials.add");
            grid.ToolbarAction("work-orders.create");
        })

        // Columns by convention (D-P6) — configure only declares the actions.
        .Grid<TimeEntryList.Result>("web.time.list", "time.list", grid =>
        {
            grid.RowAction("time.approve");
        })

        .Grid<MaterialLineList.Result>("web.materials.list", "materials.list")

        .Grid<StockList.Result>("web.stock.list", "stock.list", grid =>
        {
            grid.Column(x => x.Sku);
            grid.Column(x => x.Name);
            grid.Column(x => x.Unit);
            grid.Column(x => x.UnitPrice);
            grid.Column(x => x.IsActive);
            grid.RowAction("stock.deactivate");
            grid.ToolbarAction("stock.create");
        })

        .Build();
}
