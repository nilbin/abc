# Step 7 — Completing: an intent, not an edit *(BUILT)*

```csharp
// samples/erp/Features/Orders.cs

[Operation("orders.complete")]
[Authorize("orders.complete")]
[Widens("orders.complete-all")]
public static class CompleteOrder
{
    public sealed record Input(OrderId OrderId);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderErrors.NotFound;

        var scope = context.CheckOwnershipUnless("orders.complete-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = order.Complete();
        if (result.IsError) return result.As<Output>();

        return new Result<Output> { Output = new Output(order.Version + 1) }
            .Effect(new EventPublished("order-completed",
                new { orderId = order.Id.Value, number = order.Number.Value }));
    }
}
```

No `Change<T>` here, deliberately: status is consequential state, so it moves only through this intent — `EDIT001` fires at build time if anyone exposes an enum through a generic change-set. And note there is no extension channel either: tenant fields never ride on intents.

The effect is not a typed event class — it is `EventPublished("order-completed", payload)`, an anonymous payload against a *declared* contract: the host's model carries `.PublishesEvent("order-completed", "orderId", "number")` (Step 0's builder), which is what subscribers bind against and `PLG009` verifies (Step 17.4). The event commits through the outbox if and only if the transaction commits; the inferred `entity-modified` effects drive the grid's live refresh (decision D5); the audit trail records it all — none of it written here.

---
