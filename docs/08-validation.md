# 08 — Validation Model

Validation authority should be separated deliberately.

## Semantic type validation

Examples:

- Email shape
- Maximum intrinsic length
- Money precision
- Phone normalization

## Operation validation

Examples:

- Customer must be active
- Accounting period must be open
- Order must be editable
- Project must belong to the selected customer

## Database validation

Examples:

- Nullability
- Foreign keys
- Unique constraints
- Check constraints
- Concurrency tokens

## Derivation validation

Used for early field-level and contextual feedback:

- Customer currently inactive
- Selected project no longer available
- Reference already in use
- External VAT number invalid

## Rule reuse

The same business rule should be reused by derivation and operation execution where possible.

Example:

```csharp
var result = await OrderRules.CustomerCanReceiveOrder(
    input.CustomerId,
    context.Db,
    cancellationToken);
```

Reactive validation presents the result early. The operation executes the same rule again authoritatively inside its transaction.

## Where tenant-defined constraints fit

Tenant-defined custom fields carry **declarative** constraints only (requiredness, ranges, lengths, option sets, regex, conditional visibility). These evaluate in two of the four authorities above:

- as *semantic type validation*, via the closed set of semantic types a field can be declared with, and
- as *derivation validation*, via the portable expression AST evaluated on both client and server.

They never participate in *operation validation* — a tenant cannot express "block completion when X" as a runtime rule. If a fact becomes consequential to business behavior, it graduates to compiled code ([15-extensibility.md](15-extensibility.md)).
