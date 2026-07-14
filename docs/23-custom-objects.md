# 23 — Custom Objects (Plugin System P4)

**Status: design.** The prerequisites landed (typed JSON predicates, extension sorting — see STATUS.md); this document resolves the remaining design questions before code, the same way [22-plugins.md](22-plugins.md) preceded P1–P3/P5. Read that document's custom-objects section first; this one supersedes its sketch where they differ.

## The idea, restated

A custom object is a tenant-defined entity — "cooling units", "loaner tools", "site keys" — that gets what compiled entities get: standard operations through the *real* pipeline, permissions, forms, grids, filtering, audit, MCP tools, automation rules. The design's core move is **maximum reuse of the extension machinery**: a custom object is not a new subsystem, it is the existing channel pointed at a generic record.

## Storage: one table, all fields in the extension channel

```
custom_objects   TenantId, Key, LabelsJson, TitleField, State     (the object registry)
custom_records   Id, TenantId, ObjectKey, Version, Extensions      (every record of every object)
```

**Every field of a custom object is an extension field.** Definitions live in the *existing* `extension_fields` registry with `Entity = "custom.{key}"` — so field types, labels, constraints, EXT### validation, packaged/tenant origins, and the admin UI all work today, unchanged. Record data lives in the existing `ExtensionData` column pattern, which means:

- validation = the existing `ExtensionApplier` against the object's specs
- filtering/sorting = the typed `ext.{key}` predicates that just shipped (JsonValue/JsonNumber)
- three-way merge = the existing extension change-set semantics
- audit = the existing column capture
- rules = P5 conditions over `ext.{field}` work on custom objects immediately

Nothing new is invented for data; the object registry is the only new table besides the records themselves.

## Manifest synthesis: the overlay grows members, not just fields

The effective manifest is already *compiled model ⊕ tenant overlay*. Custom objects extend what the overlay contributes: for each active object, the manifest builder synthesizes

- operations `custom.{key}.create`, `custom.{key}.edit`, `custom.{key}.retire` — inputs are the extension change-set (+ `recordId`/`version` for edit), title from the object's labels
- a view `custom.{key}.list` — `id`, the title field, plus every declared field as extension columns; filterable/sortable mechanically
- a default form and grid binding (`IncludeExtensions`, all fields)

each tagged with a `customObject: "{key}"` origin marker (the fourth origin, after compiled/plugin/tenant-field). MCP tools and OpenAPI paths follow from the manifest as always — an agent sees `custom_coolingUnit_create` with the tenant's field schema and admin-authored descriptions, indistinguishable in shape from `orders_create`.

**Execution routing**: the executors already 404 unknown ids; they gain one more resolution step — ids matching `custom.{key}.{verb}` resolve against the tenant's object registry and dispatch to generic handlers operating on `CustomRecord`. The generic handlers run inside the normal `OperationExecutor` flow, so authorization, structural validation, gates, automation rules, transaction, audit, idempotency, and effects all apply without exception. (`ViewDefinition` gains a string entity key so synthesized views resolve their overlay per object.)

## Permissions: the D1 tension, resolved

D1 commits to a *compiled* permission catalogue; runtime-defined objects cannot mint compiled permissions. The resolution mirrors D1's own scope-qualifier design (`:own`):

- **Two compiled umbrella permissions** govern the channel: `custom.read` and `custom.manage` (plus the existing `extensions.manage` for defining objects/fields).
- **Per-object access is a grant qualifier, not a permission**: `custom.read:coolingUnit` grants read on that object only; unqualified `custom.read` grants all. `roles.define` already trims qualifiers for catalogue validation; it additionally validates object qualifiers against the object registry — the registry-as-compiler pattern again.
- `Actor.Can` gains qualifier semantics for the `custom.*` pair only; everything else is untouched.

The catalogue stays compiled and analyzable; who-sees-which-object stays tenant data. Per-object *field* permissions ride the field registry later, as docs/15 already plans for extension fields generally.

## What stays out (v1 ceilings, stated before building)

- **No tenant-defined invariants** beyond field constraints and P5 rules — an object needing real business logic is the graduation signal.
- **References** (`reference` semantic type with a target object) validate existence at write; reverse lookup views and cascades are v2.
- **No cross-object transactions** authored by tenants.
- **Performance** is bounded by JSONB until expression-index promotion ships (still the standing perf item).
- **Graduation** (custom object → compiled entity, wire names preserved via `[WireName]`, data migrated from the document to columns) is designed in docs/22's binding-time dial; tooling is P4c.

## Phasing

- **P4a**: object registry + `custom-objects.define/retire`, generic record handlers, executor routing, manifest/MCP/OpenAPI synthesis for list/create/edit, umbrella permissions, default bindings. Demo: "Kylaggregat" tracked end to end — defined at runtime, rows created in the generic grid, filtered on typed fields, audited, agent-visible.
- **P4b**: per-object grant qualifiers in roles, `retire` on records, reference fields with existence checks, title-field lookups (so custom objects work in `LookupSelect`).
- **P4c**: graduation scaffolding (generate the C# entity/operations + migration from the registry definition).

P4a is one milestone on current machinery; nothing in it waits on anything undesigned.
