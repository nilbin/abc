# Step 10 — The integration is a mapping, not a sync engine *(BUILT — `samples/fortnox`)*

In the running system the Fortnox integration is not host code at all — it is a plugin, activation- and entitlement-gated, and its whole job is one mapping onto the `orders.create` *wire* contract:

```csharp
// samples/fortnox/FortnoxPlugin.cs (trimmed)

[TamPlugin("fortnox")]
public sealed class FortnoxPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // The read footprint is a BUILD-TIME fact (docs/31 D-X3): the install screen shows exactly this.
        plugin.RequiresView("customers.lookup", "id");

        // POST /api/integrations/fortnox.orders.import — a JSON array of Fortnox orders.
        plugin.Integration(
            "orders.import", "orders.create",
            key: row => Str(row, "documentNumber"),   // the vendor id is the idempotency key
            map: MapOrderAsync);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> MapOrderAsync(
        JsonElement row, IServiceProvider services, OperationContext context, CancellationToken ct)
    {
        // Resolve the vendor's customer NAME to our id through the host's customers.lookup VIEW,
        // as the request's actor — the blessed read seam, never a host table or CLR type.
        var views = (IHostViewReader)services.GetService(typeof(IHostViewReader))!;
        var lookup = await views.RowsAsync(
            "customers.lookup",
            new Dictionary<string, string?> { ["search"] = Str(row, "customerName"), ["pageSize"] = "1" },
            context, ct);
        object? customerId = /* first row's "id", else null */;

        return new Dictionary<string, object?>
        {
            ["customerId"] = customerId,   // null → orders.create's rule fails the row (see below)
            ["orderType"] = "service",
            ["workAddress"] = Str(row, "deliveryAddress"),
            ["description"] = Str(row, "description"),
        };
    }
}
```

Imported orders execute `orders.create` — same authorization (as the request's actor), same rules, same findings, same audit. The inbox stores each source row before processing and re-maps it on every retry, so a row that failed because the customer didn't exist yet recovers — with no re-send from the partner — once the customer is created; rows that keep failing dead-letter after bounded retries. The plugin references no host CLR type: it maps to the wire contract and reads through a *declared* view footprint. That a whole external-integration capability is a removable, per-tenant-priced plugin — over the same seams as fields and gates — is the extensibility thesis at full stretch.

One scoping note: a host-authored integration can instead use the typed `IntegrationBuilder<TSource, TInput>`, where forgetting to map a required field fails model build with `INT001`. The plugin path above maps wire dictionaries — a missing field there surfaces as a validation finding on the row (retried from the inbox), never a 500 out of the endpoint.

Traffic flows the other way too, and it needs credentials. The tenant's Fortnox API key lives
in the **secrets vault** — `secrets.set` stores only Data-Protection ciphertext, and there is
deliberately no read-back operation: `secrets.list` returns keys and a set/unset flag, never a
value, not even masked; the plaintext exists transiently, in-process, only while a run executes.
Non-secret config (`fortnox.baseUrl`) is `settings.set`, readable in the clear. **Outbound
integrations** are declared like inbound ones and triggered three ways — by a committed effect,
on a schedule, or manually: `push-completed-order` fires on `order-completed`, reads the base
URL and key from the vault, POSTs the order to the accounting API, and records a run in the
per-tenant run history (with retry, backoff and dead-lettering from the same integrations
machinery). No `HttpClient` in domain code, no credential in any config file, and the SSRF
guard blocks private-network destinations unless the host explicitly opts in.

---
