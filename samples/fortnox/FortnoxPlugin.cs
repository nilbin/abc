using System.Text.Json;
using Tam;
using Tam.AspNetCore;
using Tam.Generated;

namespace Fortnox;

/// <summary>
/// An accounting integration (docs/10 + docs/22): Fortnox order exports become orders in the
/// host, and — the plugin-on-plugin edge (docs/37 D-V4) — a finalized INVOICE from the
/// invoicing plugin is pushed to Fortnox's ledger. It depends on the invoicing plugin's
/// contract (DependsOn), consuming its `invoicing.invoice-finalized` event through the generated
/// facade — never a host or sibling CLR type. Everything stays activation- and entitlement-gated,
/// and fortnox is now activatable only where invoicing is active (the L1 guard).
/// </summary>
[TamPlugin("fortnox")]
public sealed class FortnoxPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // The plugin-on-plugin dependency edge (docs/37 D-V4): fortnox consumes the invoicing
        // plugin's contract, so it declares the edge. This is what lifts PLG010 for the
        // invoice-finalized event below, and makes fortnox activatable only where invoicing is.
        plugin.DependsOn("invoicing");

        // D-X3: the read footprint is a BUILD-TIME fact — the mapper resolves customers through
        // the host lookup view, and the install screen can show exactly that.
        plugin.RequiresView("customers.lookup", "id");

        // The invoice event fortnox consumes, declared through invoicing's generated facade
        // (from the referenced invoicing.contract.json slice) — the L2 contract coupling, and
        // the payload footprint the push handler reads.
        plugin.RequiresEvent<InvoicingInvoiceFinalizedEvent>();

        // POST /api/integrations/fortnox.orders.import — a JSON array of Fortnox orders.
        // Key: the vendor document number (idempotent replays). Map: one order → orders.create
        // wire input, re-run on every retry so a late-created customer recovers the row.
        plugin.Integration(
            "orders.import", "orders.create",
            key: row => row.String("documentNumber") ?? "",
            map: MapOrderAsync);

        // OUTBOUND on event (docs/25): when an order completes, push it to Fortnox's accounting
        // API. Reads the tenant's base URL (setting) and API key (secret) from the vault.
        plugin.OutboundIntegration(
            "push-completed-order", new EventTrigger("order-completed"), PushCompletedOrderAsync);

        // OUTBOUND on a SIBLING PLUGIN's event (docs/37 D-V4): the invoicing plugin finalizes an
        // invoice, fortnox pushes it to the ledger. The trigger names invoicing's event, legal
        // only because of the declared DependsOn edge (the closed outbound-trigger PLG010 gap).
        plugin.OutboundIntegration(
            "push-finalized-invoice", new EventTrigger("invoicing.invoice-finalized"),
            PushFinalizedInvoiceAsync);

        // OUTBOUND on schedule (docs/25): poll Fortnox for new orders and hand them to the same
        // inbound import. The tenant configures the cadence via integrations.schedule.
        plugin.OutboundIntegration(
            "poll-orders", new ScheduleTrigger(), PollOrdersAsync);
    }

    private static async Task<OutboundResult> PushCompletedOrderAsync(
        IIntegrationRunContext run, CancellationToken ct)
    {
        var baseUrl = await run.Setting("fortnox.baseUrl", ct);
        var apiKey = await run.Secret("fortnox.apiKey", ct);
        if (baseUrl is null || apiKey is null)
            return OutboundResult.Failure("not-configured");   // no base URL / API key set

        var number = run.EventPayload?.String("number");
        run.Http.DefaultRequestHeaders.TryAddWithoutValidation("Access-Token", apiKey);
        var response = await run.Http.PostAsync(
            $"{baseUrl.TrimEnd('/')}/vouchers",
            new StringContent(
                JsonSerializer.Serialize(new { orderNumber = number }),
                System.Text.Encoding.UTF8, "application/json"),
            ct);
        return response.IsSuccessStatusCode
            ? OutboundResult.Success($"pushed {number}")
            : OutboundResult.Failure($"http {(int)response.StatusCode}");
    }

    private static async Task<OutboundResult> PushFinalizedInvoiceAsync(
        IIntegrationRunContext run, CancellationToken ct)
    {
        var baseUrl = await run.Setting("fortnox.baseUrl", ct);
        var apiKey = await run.Secret("fortnox.apiKey", ct);
        if (baseUrl is null || apiKey is null)
            return OutboundResult.Failure("not-configured");

        // The payload footprint is DECLARED (RequiresEvent<InvoicingInvoiceFinalizedEvent>) —
        // invoiceId rides invoicing's contract, verified against the real event at Build().
        var invoiceId = run.EventPayload?.String("invoiceId");
        run.Http.DefaultRequestHeaders.TryAddWithoutValidation("Access-Token", apiKey);
        var response = await run.Http.PostAsync(
            $"{baseUrl.TrimEnd('/')}/invoices",
            new StringContent(
                JsonSerializer.Serialize(new { invoiceId }),
                System.Text.Encoding.UTF8, "application/json"),
            ct);
        return response.IsSuccessStatusCode
            ? OutboundResult.Success($"pushed invoice {invoiceId}")
            : OutboundResult.Failure($"http {(int)response.StatusCode}");
    }

    private static async Task<OutboundResult> PollOrdersAsync(
        IIntegrationRunContext run, CancellationToken ct)
    {
        var baseUrl = await run.Setting("fortnox.baseUrl", ct);
        var apiKey = await run.Secret("fortnox.apiKey", ct);
        if (baseUrl is null || apiKey is null)
            return OutboundResult.Failure("not-configured");

        run.Http.DefaultRequestHeaders.TryAddWithoutValidation("Access-Token", apiKey);
        var response = await run.Http.GetAsync($"{baseUrl.TrimEnd('/')}/orders?status=new", ct);
        if (!response.IsSuccessStatusCode) return OutboundResult.Failure($"http {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync(ct);
        var count = JsonSerializer.Deserialize<JsonElement>(body).GetArrayLength();
        return OutboundResult.Success($"polled {count} orders");
    }

    private static async Task<IReadOnlyDictionary<string, object?>> MapOrderAsync(
        JsonElement row, IServiceProvider services, OperationContext context, CancellationToken ct)
    {
        var customerName = row.String("customerName") ?? "";

        // Resolve the external customer name to our id through the BLESSED read seam (docs/31
        // D-X3): actor mode, so the plugin sees only what the request's actor may see — and the
        // read footprint is DECLARED (RequiresView below), never a reach into pipeline internals.
        var views = (IHostViewReader)services.GetService(typeof(IHostViewReader))!;
        var lookup = await views.RowsAsync(
            "customers.lookup",
            new Dictionary<string, string?> { ["search"] = customerName, ["pageSize"] = "1" },
            context, ct);
        var customerId = lookup.FirstRow()?.String("id");

        // A row with no resolvable customer maps to a null id and fails orders.create's
        // business rule; the inbox retries it, so creating the customer later recovers it.
        return new Dictionary<string, object?>
        {
            ["customerId"] = customerId,
            ["orderType"] = "service",
            ["workAddress"] = row.String("deliveryAddress") ?? "",
            ["description"] = row.String("description") ?? "",
        };
    }
}
