# 03 — Operations

An operation is **the only way durable business state changes**.

Examples:

```
orders.create
orders.edit-details
orders.complete
customers.update-contact
invoices.post
```

An operation owns:

- Typed input
- Typed output
- Authorization
- Transaction boundary
- Business validation
- Concurrency policy
- Idempotency policy
- State changes
- Events and external intents
- Audit information

## Example

```csharp
[Operation("orders.create")]
[Authorize("orders.create")]
public static partial class CreateOrder
{
    public sealed record Input(
        CustomerId CustomerId,
        Address WorkAddress,
        OrderDescription Description);

    public sealed record Output(OrderId OrderId);

    public static async Task<Result<Output>> Execute(
        Input input,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var customer = await context.Db.Customers
            .SingleOrDefaultAsync(
                x => x.Id == input.CustomerId,
                cancellationToken);

        if (customer is null || !customer.IsActive)
            return Errors.InvalidCustomer;

        var order = Order.Create(
            input.CustomerId,
            input.WorkAddress,
            input.Description);

        context.Db.Orders.Add(order);

        return new Output(order.Id);
    }
}
```

## What the framework derives or supplies

- HTTP endpoint
- OpenAPI schema
- JSON Schema
- TypeScript client method
- MCP tool
- Authorization wrapper
- Transaction handling
- Audit envelope
- Idempotency support
- Structured errors
- Correlation and observability

## Intent operations vs. patch operations

Consequential state changes should use explicit operations. Use generic patch-style operations only for low-risk descriptive data.

Good:

```
orders.edit-details
orders.complete
orders.cancel
orders.assign-technician
```

Avoid:

```
orders.update-anything
```

This tiering matters beyond style: the descriptive/patch tier is exactly where runtime tenant-defined fields are permitted to participate ([15-extensibility.md](15-extensibility.md)). Consequential state transitions never carry tenant-defined data as inputs to their business decisions.
