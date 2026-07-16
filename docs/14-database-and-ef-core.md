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

## Composing widened queries: the TamTreeScopes rule

The global tenant filter covers the SINGLE-source query for free. The moment a view widens or
JOINS, remember: **EF's `IgnoreQueryFilters` is query-wide** — the tree-scope helpers
(`WithInherited`, `InScope`, `InNode`) opt the whole query out and re-scope only their own
side. The rule (previously only in Orders.cs/WorkOrders.cs comments; an M3 RTFM finding):

- Plain ambient query, no join → nothing to do; the global filter is the whole story.
- Join where ONE side is widened (e.g. orders join inherited customers) → the OTHER side must
  scope itself explicitly: `db.Orders.InScope(db, context.TenantId)` (subtree-aware) or
  `.InNode(context.TenantId)` (strict), or its rows silently leak across tenants.
- The widened read and the widened reference move TOGETHER: if create validated a reference
  against `WithInherited`, the list's join must use `WithInherited` too, or valid rows drop out.

The analyzer (TAM005) rejects composing widened sources with implicitly-filtered ones where it
can see it; this section is the intent behind the diagnostic.

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
