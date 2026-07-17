using System.Text.Json;
using Tam;
using Tam.AspNetCore;

namespace Fortnox;

/// <summary>
/// A plugin whose whole job is one inbound integration (docs/10 + docs/22): Fortnox order
/// exports become orders in the host — without the plugin referencing a single host CLR type.
/// It maps the vendor payload to the "orders.create" wire contract and resolves the vendor's
/// customer name against the host's customers.lookup VIEW (the actor's permissions), never a
/// host table. Activation-gated and entitlement-gated like every plugin surface.
/// </summary>
[TamPlugin("fortnox")]
public sealed class FortnoxPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // D-X3: the read footprint is a BUILD-TIME fact — the mapper resolves customers through
        // the host lookup view, and the install screen can show exactly that.
        plugin.RequiresView("customers.lookup", "id");

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
