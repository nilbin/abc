using Erp.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;

namespace Erp.Integrations;

/// <summary>External contract as Fortnox would send it — vendor shape, not our domain.</summary>
public sealed record FortnoxOrder(
    string DocumentNumber,
    string CustomerName,
    string DeliveryAddress,
    string Description);

/// <summary>
/// docs/10 made real: external payload → typed mapping → orders.create through the normal
/// pipeline. Forgetting a required mapping is INT001 at startup; DocumentNumber is the
/// idempotency key, so replays are free.
/// </summary>
public static class ImportFortnoxOrders
{
    public static void Map(WebApplication app, TamModel model)
    {
        app.MapPost("/api/integrations/fortnox.orders.import", async (
            List<FortnoxOrder> payload, HttpContext http,
            OperationExecutor executor, ErpDbContext db, CancellationToken ct) =>
        {
            var context = TamAspNetCore.BuildContext(http, model);

            // External identity resolution: vendor customer name → our CustomerId.
            var customers = await db.Customers
                .Where(x => x.IsActive)
                .Select(x => new { x.Id, x.Name })
                .ToDictionaryAsync(x => (string)ValueWrapper.Unwrap(x.Name)!, x => x.Id.Value, ct);

            var integration = new IntegrationBuilder<FortnoxOrder, CreateOrder.Input>()
                .Map(t => t.CustomerId, s => customers.GetValueOrDefault(s.CustomerName) is var id && id != default ? id : (object?)null)
                .Map(t => t.OrderType, s => "service")
                .Map(t => t.WorkAddress, s => s.DeliveryAddress)
                .Map(t => t.Description, s => s.Description)
                .IdempotencyKey(s => s.DocumentNumber)
                .Build("fortnox.orders.import", model, "orders.create");

            var results = await IntegrationRunner.Run(integration, payload, executor, context, ct);
            return Results.Json(new { results }, TamJson.Options);
        });
    }
}
