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
        foreach (var culture in new[] { "sv", "en" })
        {
            using var stream = typeof(FortnoxPlugin).Assembly
                .GetManifestResourceStream($"Fortnox.locales.{culture}.json");
            if (stream is null) continue;
            plugin.LocaleDefaults(
                culture, JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? []);
        }

        // POST /api/integrations/fortnox.orders.import — a JSON array of Fortnox orders.
        // Key: the vendor document number (idempotent replays). Map: one order → orders.create
        // wire input, re-run on every retry so a late-created customer recovers the row.
        plugin.Integration(
            "orders.import", "orders.create",
            key: row => row.GetProperty("documentNumber").GetString() ?? "",
            map: MapOrderAsync);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> MapOrderAsync(
        JsonElement row, IServiceProvider services, OperationContext context, CancellationToken ct)
    {
        var customerName = row.GetProperty("customerName").GetString() ?? "";

        // Resolve the external customer name to our id through the host lookup VIEW — as the
        // request's actor, so the plugin sees only what that actor may see (never a host table).
        var views = (ViewExecutor)services.GetService(typeof(ViewExecutor))!;
        var (lookup, _) = await views.ExecuteAsync(
            "customers.lookup",
            new Dictionary<string, string?> { ["search"] = customerName, ["pageSize"] = "1" },
            context, ct);
        object? customerId = null;
        if (lookup?.Rows.FirstOrDefault() is { } match
            && JsonSerializer.SerializeToElement(match, TamJson.Options)
                .TryGetProperty("id", out var idElement))
            customerId = idElement.GetString();

        // A row with no resolvable customer maps to a null id and fails orders.create's
        // business rule; the inbox retries it, so creating the customer later recovers it.
        return new Dictionary<string, object?>
        {
            ["customerId"] = customerId,
            ["orderType"] = "service",
            ["workAddress"] = row.GetProperty("deliveryAddress").GetString() ?? "",
            ["description"] = row.GetProperty("description").GetString() ?? "",
        };
    }
}
