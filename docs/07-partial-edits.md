# 07 — Partial and Conflict-Safe Edits

Edit forms must not submit the entire model. They should submit only values actually changed by the user.

## Typed change sets

```csharp
public sealed record Change<T>(
    T? Original,
    T? Value);
```

- A missing `Change<T>` means **untouched**.
- A present change with `Value = null` means **explicitly clear the value**.

Example:

```csharp
[Operation("orders.edit-details")]
public static partial class EditOrderDetails
{
    public sealed record Input(
        OrderId OrderId,
        Change<OrderDescription>? Description = null,
        Change<DateOnly?>? RequestedDate = null,
        Change<Address?>? WorkAddress = null);

    public sealed record Output(long Version);
}
```

Wire format:

```json
{
  "orderId": "order-123",
  "changes": {
    "description": {
      "original": "Repair pump",
      "value": "Replace pump"
    },
    "requestedDate": {
      "original": "2026-07-20",
      "value": null
    }
  }
}
```

## Three-way merge

For every submitted field:

```
Base value      = value when the form loaded
Current value   = value currently persisted
Submitted value = new value from the user
```

Rules:

```
If current equals base:
    apply submitted
If current equals submitted:
    treat as already resolved
If current differs from base and submitted:
    return a real field conflict
```

This allows non-overlapping concurrent edits to merge automatically.

Example:

- User A changes `Description`
- User B changes `RequestedDate`

Both changes can succeed even if the row version changed.

A true conflict:

```
Base:      Repair pump
Current:   Replace pump
Submitted: Inspect pump
```

Return structured conflict data:

```json
{
  "code": "concurrency.field-conflict",
  "conflicts": [
    {
      "field": "description",
      "originalValue": "Repair pump",
      "currentValue": "Replace pump",
      "submittedValue": "Inspect pump"
    }
  ]
}
```

The frontend can offer:

- Keep current
- Use mine
- Review manually

## Equality

Conflict detection and dirty tracking must use **semantic equality**.

Examples:

- Normalized email comparison
- Phone number normalization
- Address value equality
- Decimal scale normalization
- Case-insensitive identifiers where applicable

Value types should supply their own equality and normalization.

Complex semantic values should be **atomic by default** — for example, an `Address` either changed or it did not. Nested field-level address merging should be opt-in.

## Cross-feature edit forms

If one form edits order description and customer phone, the binding should produce **two change sets**:

```
orders.edit-details
customers.update-contact
```

Transaction behavior must be explicit:

- **Independent mode** — each operation commits separately.
- **Composite mode** — a dedicated composite operation applies both atomically.

The form must never implicitly perform generic cross-entity persistence.

## Extension fields

Tenant-defined fields participate in exactly the same machinery: the same `Change<T>` semantics on the wire, the same three-way merge, and semantic equality supplied by each field's semantic type. See [15-extensibility.md](15-extensibility.md).
