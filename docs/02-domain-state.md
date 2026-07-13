# 02 — Domain State

Normal C# entities, value objects, domain rules, and EF Core persistence.

The framework must **not** require:

- Framework base classes
- Dynamic property bags
- Generic repositories
- Separate persistence and domain models unless genuinely necessary
- A parallel semantic entity registry

> Note: tenant-extensible entities opt in to a single framework-managed, schema-validated extension container — see [15-extensibility.md](15-extensibility.md). This is deliberately not a free-form property bag: domain code cannot write arbitrary keys into it, and it exists only at explicitly declared extension points.

## Example

```csharp
public sealed class Order
{
    public OrderId Id { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public Address WorkAddress { get; private set; }
    public OrderDescription Description { get; private set; }
    public OrderStatus Status { get; private set; }

    public void ChangeDescription(OrderDescription description)
    {
        Description = description;
    }

    public Result Complete()
    {
        if (Status == OrderStatus.Completed)
            return Errors.AlreadyCompleted;

        Status = OrderStatus.Completed;
        return Result.Success();
    }
}
```

## Semantic value types

Value types should carry intrinsic meaning where appropriate:

```csharp
[Label("Email")]
[Format("email")]
public readonly record struct EmailAddress(string Value);

[Label("Order description")]
[Multiline]
public readonly record struct OrderDescription(string Value);
```

Semantic value types are the single authority for:

- Intrinsic shape validation (email format, max intrinsic length, money precision)
- Normalization (email casing, phone number formats, decimal scale)
- Semantic equality (used by dirty tracking and three-way merge — see [07-partial-edits.md](07-partial-edits.md))
- Default labels and formatting hints

The same semantic type vocabulary is reused by runtime-defined tenant fields ([15-extensibility.md](15-extensibility.md)), so a tenant-defined "email" field validates, normalizes, and compares exactly like a compiled `EmailAddress`.
