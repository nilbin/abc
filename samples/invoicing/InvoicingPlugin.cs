using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;
using Tam.Generated;

namespace Invoicing;

/// <summary>
/// Tutorial Step 17 (docs/31): the plugin that EXTENDS ANOTHER DOMAIN. Invoicing becomes part
/// of the host's Orders surface — a row action on the host grid, an invoice-status field on the
/// order, drafts written by order events — with zero host edits beyond the storage opt-in, and
/// no host CLR type anywhere in this assembly.
/// </summary>
[TamPlugin("invoicing")]
public sealed class InvoicingPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.Model.AddDiscovered();
        plugin.LocaleDefaults();

        // D-X3: compatibility as a build-time fact — the views and fields this plugin reads.
        // PLG008 verifies them; service-mode reads (the draft subscriber) are limited to them.
        plugin.RequiresView("orders.detail", "id", "number", "estimatedTotal");

        // D-X2 read half (docs/22 P2): the order wears its invoice status. The write half is
        // IPackagedFieldWriter in the operations/subscriber — structurally scoped to this key.
        plugin.ExtensionField("order", "invoiceStatus", "selection",
            options: ["draft", "invoiced", "paid"]);

        // D-X1: "Create invoice" where the user lives — a row action on the HOST's orders
        // grid, with a declared input↔column bind. Entitlement+activation+permission gate it.
        plugin.GridAction("web.orders.list", "invoicing.create-from-order",
            bind => bind.Field("orderId", fromColumn: "id"));

        // Step 17 beat 4: the draft writes itself when the host commits an order completion.
        // The payload shape the handler reads is a CONTRACT, not folklore (docs/31 D-X5).
        plugin.RequiresEvent("order-completed", "orderId", "number");
        plugin.OnEffect<DraftOnCompletion>("order-completed");
        plugin.Model
            .PublishesEvent("invoicing.invoice-created", "invoiceId", "orderId")
            .PublishesEvent("invoicing.invoice-finalized", "invoiceId", "orderId")
            .PublishesEvent("invoicing.invoice-paid", "invoiceId", "orderId");

        // Step 17 beat 5: invoicing pushes back — an order with a pending DRAFT invoice cannot
        // complete ("you started invoicing this order; finish it first"). Own data only.
        plugin.Gate<DraftPendingGate>("orders.complete");

        // D-X4: the invoice panel on the order detail — the plugin's own grid bound to the
        // slot's record context. The host opted the surface in once; it never names us.
        plugin.Panel("web.orders.detail", grid: "invoicing.web.invoices",
            bind => bind.Query("orderId", fromContext: "orderId"));

        plugin.Nav(nav => nav.Page("invoicing.invoices",
            grid: "invoicing.web.invoices", suggest: "work", order: 40));

        plugin.Model.Grid<InvoiceList.Result>(
            "invoicing.web.invoices", "invoicing.invoices.list", grid =>
        {
            grid.Column(x => x.OrderNumber);
            grid.Column(x => x.Status);
            grid.Column(x => x.Amount);
            grid.Column(x => x.Created);
            grid.RowAction("invoicing.finalize");
            grid.RowAction("invoicing.mark-paid");
        });
    }

    /// <summary>Blocks orders.complete while a draft invoice exists for the order — the gate
    /// reads the plugin's OWN table off the wire input, never host types (docs/22 P2).</summary>
    private sealed class DraftPendingGate(ITamDb tam) : IOperationGate
    {
        public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
        {
            if (!gate.Input.TryGetProperty("orderId", out var idProp)
                || !Guid.TryParse(idProp.GetString(), out var orderId))
                return Result.Success();
            var pending = await tam.Db.Set<Invoice>()
                .AnyAsync(x => x.OrderId == orderId && x.Status == "draft", ct);
            return pending ? InvoicingFindings.DraftPending.Create() : Result.Success();
        }
    }

    /// <summary>
    /// Step 17 beat 4: on order completion, draft the invoice. Idempotent (at-least-once
    /// delivery); order number from the payload; the amount backfilled through a SERVICE-MODE
    /// declared read (docs/31 D-X3) — no actor exists here, so the readable surface is exactly
    /// the RequiresView list. The status lands on the order via the writer (D-X2).
    /// </summary>
    private sealed class DraftOnCompletion(
        ITamDb tam, IHostViewReader host, IPackagedFieldWriter fields) : IEffectHandler
    {
        public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
        {
            if (!effect.Payload.TryGetProperty("orderId", out var idProp)
                || !Guid.TryParse(idProp.GetString(), out var orderId))
                return;
            if (await tam.Db.Set<Invoice>().AnyAsync(x => x.OrderId == orderId, ct))
                return;   // redelivery or already invoiced by hand — idempotent

            var number = effect.Payload.TryGetProperty("number", out var n)
                ? n.GetString() ?? "" : "";
            var amount = 0m;
            var detail = await host.RowsAsync("orders.detail",
                new Dictionary<string, string?> { ["orderId"] = orderId.ToString() }, ct);
            if (detail.Rows.Count > 0)
            {
                var row = System.Text.Json.JsonSerializer.SerializeToElement(
                    detail.Rows[0], TamJson.Options);
                if (row.TryGetProperty("estimatedTotal", out var a)
                    && a.ValueKind == System.Text.Json.JsonValueKind.Number)
                    amount = a.GetDecimal();
            }

            tam.Db.Add(Invoice.Create(effect.TenantId, orderId, number, amount));
            await tam.Db.SaveChangesAsync(ct);
            await fields.SetAsync("order", orderId, "invoiceStatus", "draft", ct);
        }
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
