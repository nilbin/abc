# Step 2 — The create operation *(BUILT)*

The only way an order comes into existence, no matter who calls.

```csharp
// samples/erp/Features/Orders.cs

[Operation("orders.create")]
[Authorize("orders.create")]
[AcceptsExtensions(typeof(Order))]
public static class CreateOrder
{
    public sealed record Input(
        [property: LabelKey("labels.customer")] CustomerId CustomerId,
        OrderType OrderType,
        Address WorkAddress,
        OrderDescription Description,
        [property: LabelKey("labels.project")] ProjectId? ProjectId = null,
        DateOnly? RequestedDate = null,
        decimal? EstimatedTotal = null);

    public sealed record Output(OrderId OrderId, OrderNumber Number);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var customerCheck = await OrderRules.CustomerCanReceiveOrder(
            input.CustomerId, context.TenantId, db, ct);
        if (customerCheck.IsError) return customerCheck.As<Output>();

        if (input.OrderType == OrderType.Project)
        {
            var projectCheck = await OrderRules.ProjectBelongsToCustomer(
                input.ProjectId, input.CustomerId, db, ct);
            if (projectCheck.IsError) return projectCheck.As<Output>();
        }

        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;
        var sequence = await db.Orders.CountAsync(ct) + 1412;   // the demo's number series
        var order = Order.Create(
            context.TenantId.Value,
            new OrderNumber($"{year}-{sequence:D5}"),
            input.CustomerId,
            input.OrderType,
            input.OrderType == OrderType.Project ? input.ProjectId : null,
            input.WorkAddress,
            input.Description,
            input.RequestedDate,
            input.EstimatedTotal);

        db.Orders.Add(order);
        return new Output(order.Id, order.Number);
    }
}
```

The shape to notice: the handler's parameter list *is* its dependency declaration — the wire input, the ambient `OperationContext` (actor, tenant, culture, idempotency key; **no `.Db`**, no service locator), the application's own `ErpDbContext` injected like any service, and a `CancellationToken`. `[AcceptsExtensions(typeof(Order))]` opens the tenant-field channel (Step 9). A `Result` from a shared rule narrows to `Result<Output>` with `.As<Output>()`; a bare `Output` converts implicitly.

The business rules live once, shared with derivations (Step 3):

```csharp
public static class OrderRules
{
    public static async Task<Result> CustomerCanReceiveOrder(
        CustomerId customerId, TenantId tenant, ErpDbContext db, CancellationToken ct)
    {
        // Customers are group-shared reference data (Step 15) — hence the explicit scope.
        var customer = await db.Customers.WithInherited(db, tenant)
            .Where(x => x.Id == customerId)
            .Select(x => new { x.IsActive })
            .SingleOrDefaultAsync(ct);
        return customer is { IsActive: true } ? Result.Success() : OrderErrors.InvalidCustomer;
    }
    // ProjectBelongsToCustomer(...) similar
}
```

**Derived from this file alone** — no further code:

| Artifact | Result |
| --- | --- |
| HTTP endpoint | `POST /api/operations/orders.create` |
| OpenAPI + JSON Schema | Input/Output schemas from the records; nullability = requiredness |
| TypeScript client | `client.ordersCreate(input): Promise<TypedOperationResponse<OrdersCreateOutput>>` — flat camelCase methods over operation ids, generated from the manifest |
| MCP tool | `orders_create` with the same schema (Step 8) |
| Pipeline | authorization, transaction, structural validation, audit entry, idempotency (the `X-Idempotency-Key` header), correlation, `TenantId` stamping |
| Permission catalogue | `orders.create` appears in the manifest's catalogue; roles validate against it at define time (Step 15) |

The wire envelope every caller gets back:

```json
{
  "output": { "orderId": "7c9e1c1a-4b2e-4f5e-9d3a-0b54c7e3a1f2", "number": "2026-01417" },
  "findings": [],
  "effects": [ { "type": "entity-created", "entity": "order", "id": "7c9e1c1a-4b2e-…" } ],
  "newVersion": 0,
  "auditReference": "8f4c2f0b6c7d4e21a3b90d2f4a5b6c7d"
}
```

Effects name entities by their wire key — the kebab-cased CLR name (`order`), the same key Step 9's field registry and Step 13's packaged fields address. And `auditReference` points into `audit.entries`, which is itself an ordinary queryable view with a shipped admin grid (`web.audit.list`) — the audit trail is read through the same machinery it audits.

---
