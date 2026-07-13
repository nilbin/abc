# 14 — Database and EF Core

Do not replace EF Core.

EF Core remains responsible for:

- Persistence mapping
- Relationships
- Query translation
- Transactions
- Migrations
- Change tracking
- Concurrency tokens

The framework should inspect and integrate with EF Core.

## Consistency verification

It should verify consistency between:

```
C# types
Operation contracts
View contracts
Semantic value rules
EF Core model
Database constraints
```

Example build error:

```
CreateOrder.Input.Description supports 1,000 characters,
but Order.Description is persisted as varchar(500).
```

## Migrations

Migrations must remain reviewable.

The framework may generate scaffolding or diagnostics, but it must not silently mutate production schemas.

This constraint is also why tenant-defined custom fields do **not** use runtime DDL (`ALTER TABLE`): extension data lives in one JSONB column per extensible aggregate, declared once in a reviewable migration, with expression indexes promoted deliberately for hot fields ([15-extensibility.md](15-extensibility.md)).
