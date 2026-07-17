# Step 9 — The tenant adds a custom field. Nobody deploys anything. *(BUILT)*

A tenant administrator (or an agent on their behalf) calls a framework operation — the registry is itself just operations:

```json
POST /api/operations/extensions.define-field
{
  "entity": "order",
  "key": "machineSerialNumber",
  "type": "text",
  "maxLength": 40,
  "labels": { "sv": "Maskinserienummer", "en": "Machine serial number" },
  "descriptions": {
    "sv": "Serienummer för den servade maskinen, från typskylten.",
    "en": "Serial number of the serviced machine, from the type plate."
  }
}
```

The entity is the wire key from Step 2's effects (`order`); the payload is exactly the registry's surface — key, type, labels, `required?`, `maxLength?`, `options?` for selections. The registry runs its checks at definition time — unknown entity (`EXT007`), unknown type, malformed key, key collision (`EXT005`), missing default-culture label (`EXT006`, the registry twin of `L10N001`) — then activates the field and bumps the tenant's manifest revision. *(Designed, not built: declarative placement and per-field write permissions — today the field splices in wherever a binding put its `.Extensions()` marker, and writes ride the operation's own permission.)*

Because every binding in this tutorial opted in with `form.Extensions()` / `grid.Extensions()`, the field is **immediately**:

- an input on the web create and edit forms at the splice point, validated to 40 characters, rendered by the standard `text` renderer;
- a column and filter in the orders grid (`?ext.machineSerialNumber=…` — JSON-translated on the database, equality/range/contains operators derived from the declared type);
- carried in `orders.edit-details` submissions as `"extensions": { "machineSerialNumber": { "original": null, "value": "MX-55012" } }` — the same change-set shape, three-way merged with structured conflicts;
- in the audit trail with the operation that wrote it *(per-key audit granularity is designed, not built — today the audit entry records the extension column's change)*;
- in the MCP tool schema with the admin's description — agents read and write it like any field.

What it can never do: gate `orders.complete`, appear on an intent operation, or otherwise steer compiled business decisions — the pipeline holds that line (see [15-extensibility.md](../15-extensibility.md)). *(Designed, not built: the graduation scaffold that promotes a load-bearing field to a compiled property with a data migration.)*

Custom fields have a sibling: **automation rules** — the tenant's declarative logic, bounded by
the same trust line. An admin defines "cold-chain orders need a requested date" as data: a Px
condition over the input (compiled and extension fields alike) and a blocking finding, validated
at definition time (`RUL001` unknown operation, `RUL002` unknown field, `RUL003` missing
default-culture message), evaluated without loops, code, or HTTP — and fully audited.
Conditions also read the operation's TARGET row (`row.budget`, `row.status`, `row.ext.key`) —
so "big open projects can't be closed" works on an intent that carries only an id; the target
resolves from the `{entity}Id` input at define time (`RUL004` when there is no single target),
loads read-only pre-transaction, and compares wire-identically (money as numbers, enums as
their wire strings — docs/22). Two sharp edges worth knowing before you author one: the
condition's wire shape and operator set are exactly docs/22's (discriminator `t`; a firing
rule's finding is `rules.{name}`), and EDIT001 is broader than the status examples suggest —
the analyzer refuses ANY enum inside a `Change<T>`, so "let users change priority later" is
an intent operation (`work-orders.set-priority`), never an edit field. The
executor has no rules special case: rules run as the `tam.rules` package's own wildcard gate,
through the very seam Step 16's approvals plugin uses; and because Px is portable, the same
condition drives client-side form behavior without a round trip. What rules never get is
arbitrary code or writes to compiled fields — EDIT001's philosophy, extended to tenants.

---
