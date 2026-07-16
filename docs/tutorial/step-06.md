# Step 6 — Editing: partial, conflict-safe *(BUILT)*

```csharp
// samples/erp/Features/Orders.cs

[Operation("orders.edit-details")]
[Authorize("orders.edit")]
[Widens("orders.edit-all")]
[AcceptsExtensions(typeof(Order))]
public static class EditOrderDetails
{
    public sealed record Input(
        OrderId OrderId,
        Change<OrderDescription?>? Description = null,
        Change<DateOnly?>? RequestedDate = null,
        Change<Address?>? WorkAddress = null,
        Change<decimal?>? EstimatedTotal = null);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderErrors.NotFound;

        // The write-side twin of the list's scope: base-atom holders edit only their own orders.
        var scope = context.CheckOwnershipUnless("orders.edit-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        if (order.Status != OrderStatus.Open) return OrderErrors.NotEditable;

        var merge = TamMerge.Apply(order, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();

        return new Output(order.Version + 1);
    }
}
```

Read the `Change` types precisely, because the convention carries meaning: `Change<OrderDescription?>?` is nullable **inside and out**. The outer `?` means *absent = untouched* — a partial edit names only what the user touched. The inner `?` lets a present change carry `value: null` — an explicit clear. Every clearable field follows this double-nullable convention; a non-clearable field would keep its inner type non-nullable. Loading is ordinary EF through the global tenant filter — `SingleOrDefaultAsync` plus a not-found finding, no special load API — and the handler states the *business* precondition (only open orders are editable). Everything mechanical — dirty detection, three-way merge, semantic equality per value type, conflict shaping, field-level audit — is `TamMerge` and the pipeline.

Two dispatchers edit order `2026-01415` concurrently. Anna changes the description; Björn changed the requested date a moment earlier. Anna's submission — change fields ride *flat* on the input, there is no `changes` wrapper:

```json
{
  "orderId": "0b54c7e3-9d3a-4f5e-8b2e-7c9e1c1a41f2",
  "description": { "original": "Repair pump", "value": "Replace pump" }
}
```

Current `description` still equals Anna's original → her change applies cleanly even though the row version moved. Had both edited the description, she'd get — instead of an exception page —

```json
{
  "findings": [ {
    "code": "concurrency.field-conflict", "severity": "error",
    "targets": ["description"], "blocksSubmission": true
  } ],
  "conflicts": [ {
    "field": "description",
    "originalValue": "Repair pump",
    "currentValue": "Overhaul pump assembly",
    "submittedValue": "Replace pump"
  } ]
}
```

— which the form runtime renders as *keep current / use mine / review*.

The edit form binding lives beside the create form in the composition root (`web.orders.edit`, Step 4's pattern, with the record key hidden); the record surface prefills it from the `orders.detail` view (Step 18).

---
