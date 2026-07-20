# 07 — Partial and Conflict-Safe Edits

Generated edit forms submit **every initialized `Change<T>` field** — not a sparse subset (docs/40,
round 8). The *effective patch* is derived from the values, not from which fields are present: a field
whose `Original == Value` is a **no-op** the merge ignores. This removes any resolve-vs-submit
divergence; partial persistence and field-level concurrency fall out of the three-way merge, not out of
a hand-pruned payload.

## Typed change sets

```csharp
public sealed record Change<T>(
    T? Original,
    T? Value);
```

- `Original == Value` (semantically) means **untouched** — a no-op that is neither validated, written,
  nor concurrency-checked.
- `Original != Value` is the **effective patch** for that field.
- `Value == null` with a non-null `Original` means **explicitly clear** the value.
- A **missing** `Change<T>` means the caller did not project/initialize that field at all (e.g. a direct
  API call that omits it) — *not* the general definition of "untouched", which is `Original == Value`.
- A null `Original` is a **valid merge base** (the field loaded null), never inferred to be "missing".

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

Wire format — **flat operation fields**, each `Change<T>` an `{original, value}` object (there is no
nested `changes` envelope). Every initialized change field is present, including untouched ones (here
`requestedDate`, whose `original == value`):

```json
{
  "orderId": "order-123",
  "description": {
    "original": "Repair pump",
    "value": "Replace pump"
  },
  "requestedDate": {
    "original": "2026-07-20",
    "value": "2026-07-20"
  }
}
```

## Three-way merge

For every submitted field:

```
Base value      = Original (value when the form loaded)
Current value   = value currently persisted
Submitted value = Value (new value from the user)
```

Rules (evaluated in order):

```
If base equals submitted (Original == Value):
    no effective change — no write, no concurrency check (the FIRST branch)
If current equals submitted:
    treat as already resolved
If current equals base:
    apply submitted
Otherwise (current differs from both):
    return a real field conflict
```

The `Original == Value` branch is what makes complete-state submission safe: an untouched field a
concurrent writer changed does not surface as a conflict, because the user never touched it.

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
