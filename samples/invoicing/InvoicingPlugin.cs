using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.Generated;

namespace Invoicing;

/// <summary>
/// Tutorial Step 17 (docs/31): the plugin that EXTENDS ANOTHER DOMAIN — and the authoring-shape
/// showcase (review round 4): behaviors register from their own attributes ([Gate]/[OnEffect]
/// in Features.cs, discovered like [Operation]/[View]), and Configure is a TABLE OF CONTENTS
/// over cohesive PARTS. Zero host edits beyond the storage opt-in; no host CLR type anywhere.
/// </summary>
[TamPlugin("invoicing")]
public sealed class InvoicingPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.AddDiscovered();   // operations, views, [Gate]/[OnEffect] behaviors
        plugin.AddPart<OrdersContract>();   // everything host-facing
        plugin.AddPart<InvoiceSurface>();   // the plugin's own UI
    }

    /// <summary>Registers the plugin's tables on the host's DbContext — the one-line opt-in.</summary>
    public static ModelBuilder AddInvoicing(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("invoicing_invoices");
            b.HasKey(x => x.Id);
            b.Property(x => x.OrderNumber).HasMaxLength(50);
            b.HasIndex(x => new { x.TenantId, x.OrderId });
        });
        return modelBuilder;
    }
}

/// <summary>
/// The HOST-FACING contract in one place: what this plugin reads (D-X3), the field it puts on
/// the order (D-X2), where it appears on host surfaces (D-X1/D-X4), and the events it consumes
/// and publishes (D-X5). This part IS the install screen's story.
/// </summary>
internal sealed class OrdersContract : IPluginPart
{
    public void Configure(PluginBuilder plugin)
    {
        // Compatibility as a build-time fact — the views/fields this plugin reads (PLG008);
        // service-mode reads (the draft subscriber) are limited to exactly this list.
        plugin.RequiresView("orders.detail", "id:guid", "number", "estimatedTotal:decimal");

        // The field-service path (docs/34 M4): a completed work order is invoiced from its
        // APPROVED time + materials — service-mode reads over the M3 views, filtered by the
        // number the event payload carries.
        plugin.RequiresView("time.list", "id:guid", "workOrderNumber", "amount:decimal", "status");
        plugin.RequiresView("materials.list", "id:guid", "workOrderNumber", "amount:decimal");

        // The order wears its invoice status (docs/22 P2). Read-only: only this plugin's
        // IPackagedFieldWriter sets it — the state machine stays the plugin's.
        plugin.ExtensionField("order", "invoiceStatus", "selection",
            options: ["draft", "invoiced", "paid"], readOnly: true);

        // "Create invoice" where the user lives — a row action on the HOST's orders grid,
        // with a declared input↔column bind. Entitlement+activation+permission gate it.
        plugin.GridAction("web.orders.list", "invoicing.create-from-order",
            bind => bind.Field("orderId", fromColumn: "id"));

        // The invoice panel on the order detail — the plugin's own grid bound to the slot's
        // record context. The host opted the surface in once; it never names us.
        plugin.Panel("web.orders.detail", grid: "invoicing.web.invoices",
            bind => bind.Query("orderId", fromContext: "orderId"));

        // Event contracts (docs/31 D-X5): payload shapes are declared, never folklore. The
        // DraftOnCompletion subscriber and DraftPendingGate register from their own
        // attributes in Features.cs.
        plugin.RequiresEvent("order-completed", "orderId:guid", "number");
        plugin.RequiresEvent("work-order-completed", "workOrderId:guid", "number");
        plugin
            .PublishesEvent("invoicing.invoice-created", "invoiceId:guid", "orderId:guid")
            .PublishesEvent("invoicing.invoice-finalized", "invoiceId:guid", "orderId:guid")
            .PublishesEvent("invoicing.invoice-paid", "invoiceId:guid", "orderId:guid");
    }
}

/// <summary>The plugin's own UI: its grid and its nav suggestion.</summary>
internal sealed class InvoiceSurface : IPluginPart
{
    public void Configure(PluginBuilder plugin)
    {
        // The plugin's OWN declared page (review round 4): grid + a read-only record surface
        // (no form — status moves through the finalize/mark-paid operations, never an edit).
        plugin.Page("invoicing.invoices", page => page
            .Grid("invoicing.web.invoices")
            .Record(record => record
                .Detail("invoicing.invoices.detail", key: "invoiceId")
                .Title("orderNumber")));

        plugin.Nav(nav => nav.Page("invoicing.invoices",
            page: "invoicing.invoices", suggest: "work", order: 40));

        // Columns default to the result record (docs/32); only the ACTIONS are a decision.
        plugin.Grid<InvoiceList.Result>(
            "invoicing.web.invoices", "invoicing.invoices.list", grid =>
        {
            grid.RowAction("invoicing.finalize");
            grid.RowAction("invoicing.mark-paid");
        });
    }
}
