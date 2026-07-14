# 15 — Tenant Extensibility and Custom Fields

## The problem

Tenants need to customize the application without deployments. The canonical case: a tenant administrator adds a "Machine serial number" field to Orders, and it must immediately appear — typed, validated, permission-checked — in create/edit forms, grids, reports, exports, the audit trail, MCP tool schemas, and (if mapped) integrations.

This collides head-on with the framework's core commitments: compiled manifests, typed operation inputs, no dynamic property bags, no runtime metadata server. A naive implementation destroys exactly what makes the framework worth building.

## Design principle: two authoring channels, one model

The resolution is to treat tenant customization not as a bolt-on dynamic system but as a **second authoring channel into the same model**:

| | Compiled fields | Tenant extension fields |
| --- | --- | --- |
| Authored in | C# (records, value types, attributes) | Tenant field registry (admin UI / API) |
| Authored when | Compile time | Runtime, per tenant |
| Verified by | Roslyn compiler + analyzers | Registry service running the **same rule set** at definition time |
| Described by | Compiled manifest | Tenant overlay (same field-descriptor shape) |
| Semantic types | C# value types (`EmailAddress`, `Money`, …) | The **same** semantic type implementations, referenced by key |
| Validation | Semantic + operation + derivation | Semantic + declarative constraints + portable expressions |
| Rendered by | Generic manifest-driven runtime | The same runtime — descriptors are indistinguishable |

The one-line summary:

> **The tenant field registry is the compiler for runtime-authored facts.** Same semantic types, same field descriptors, same diagnostics, same findings pipeline — different authoring time.

A second principle bounds the blast radius:

> **Extension fields live in the descriptive tier.** The plan already distinguishes intent operations (`orders.complete`) from low-risk descriptive editing (`orders.edit-details`). Runtime-defined fields are permitted *only* in the descriptive tier: they can be stored, displayed, validated, filtered, exported, and mapped — but they can never be inputs to compiled business decisions. When a fact becomes consequential, it graduates to code (see [Graduation path](#graduation-path)).

## Extension points are opt-in

A developer declares that an aggregate is extensible:

```csharp
public sealed class Order : IExtensible
{
    // ... normal typed properties ...
    public ExtensionData Extensions { get; private set; }
}
```

`ExtensionData` is a framework value object wrapping a schema-validated document, persisted as one JSONB column. It is deliberately **not** a free-form property bag:

- Domain code cannot write arbitrary keys — writes flow through the extension pipeline, which validates against the tenant's field registry.
- Reads from handler code (rare) use typed accessors: `order.Extensions.Get<Money>(fieldRef)`.
- Non-extensible entities carry no container and pay no cost.

This satisfies the spirit of the "no dynamic property bags" rule: the *domain model* stays typed and intentional; the dynamic surface exists only at declared extension points, under framework control.

## The tenant field registry

A system-level aggregate managed through **normal framework operations** (`extensions.define-field`, `extensions.update-field`, `extensions.retire-field`) — the registry dogfoods the operation pipeline, so definitions are authorized, transactional, audited, and versioned like any other business change.

```csharp
public sealed record ExtensionFieldDefinition(
    ExtensionFieldId Id,             // stable identity, never reused
    ExtensionFieldKey Key,           // wire/storage key, e.g. "machineSerialNumber"
    ExtensionTarget Target,          // e.g. entity "orders.order"
    SemanticTypeKey Type,            // from the closed semantic type set
    bool Required,                   // enforced on new writes; see lifecycle
    ConstraintSet Constraints,       // declarative: length, range, regex, options, scale
    PortableExpression? Visibility,  // same AST as portable derivations
    PortableExpression? Requiredness,
    LocalizedText Labels,
    LocalizedText? Description,      // also becomes the MCP schema description
    FieldPlacement Placement,        // section/order hints + per-binding-class inclusion
    FieldPermissions Permissions,    // read/write per role
    ExtensionFieldState State)       // Draft → Active → Deprecated → Retired
```

### Closed semantic type set

Tenant fields choose from a closed set of semantic types mirroring the compile-time vocabulary:

```
Text, MultilineText, Number(scale), Money, Percentage,
Date, DateTime, Bool,
Selection(optionSet), MultiSelection(optionSet),
Email, Phone, Url,
Reference(entityKey)
```

Each maps to the **same implementation** used by compiled value types — same normalization, same semantic equality, same formatting, same renderer key. `Reference(entityKey)` stores a typed id and resolves display values through the target entity's lookup view, so a tenant can add "Responsible technician" referencing Employee and get a working picker and grid column for free.

No tenant-defined types, no tenant-defined logic. The set can grow, but only in compiled code.

### Declarative constraints and portable rules

The agreed scope is **fields + validation + visibility**:

- **Constraints** are data: min/max length, numeric range, regex, option sets, decimal scale. Evaluated by the semantic-type layer on both client and server.
- **Visibility / conditional requiredness** are expressed in the **portable expression AST** — the same deliberately constrained model that `[PortableDerivation]` C# lowers to ([05-derivations.md](05-derivations.md)). Compiled rules are authored in C# and lowered by the source generator; tenant rules are authored as AST data via the admin UI. One AST, one client evaluator, one server evaluator, one cycle detector.
- Violations surface as ordinary `Finding`s with `FieldPath` values like `extensions.machineSerialNumber` — the findings pipeline needs no changes.

What tenants explicitly **cannot** author: arbitrary code, cross-entity queries, effects, or operation-blocking business rules. `BlocksSubmission` for extension findings is limited to the field's own declarative constraints.

### Registry-time diagnostics

The registry runs the same rule implementations the compiler runs, at definition time — before a change activates:

```
EXT001: Required field "machineSerialNumber" is hidden by its visibility rule
        for all order types.                          (FORM001's runtime twin)
EXT002: Visibility rule creates a dependency cycle with "warrantyClass".
EXT003: Option set change orphans values present on 1,240 orders.
EXT004: Integration "fortnox.orders.export" maps retired field "oldRef".
EXT005: Field key conflicts with a compiled field or reserved retired key.
EXT006: Field lacks a label for enabled culture "en".  (L10N001's registry twin)
```

A definition that fails its diagnostics is rejected, exactly as code that fails analyzers does not compile.

## The effective manifest

```
Effective manifest (per tenant, cached, revisioned)
  = compiled manifest
  ⊕ tenant overlay        (extension fields + customization of compiled fields)
  ⊕ permissions           (per user/role, filters and locks fields)
  ⊕ user preferences
```

Key properties:

- The overlay produces **the same field-descriptor shape** the compiler produces. Downstream consumers — forms runtime, grid runtime, OpenAPI, MCP adapter, export writers — consume the effective manifest and cannot tell the channels apart.
- The overlay carries a **monotonic revision**. Every API response includes the revision; clients refresh descriptors on mismatch. Long-lived form sessions tolerate skew: unknown-to-client fields are simply untouched on submit.
- The overlay is small and cacheable per tenant; there is still no "large runtime metadata server" — just registry rows merged over a build artifact.

### Customizing compiled fields, too

Per-tenant customization is not only *new* fields. The same overlay may, within safe bounds checked by `EXT###` diagnostics:

- Relabel compiled fields and sections (tenant terminology)
- Hide optional compiled fields (never required-without-default ones — FORM001's rule again)
- Reorder fields and adjust placement/sections
- Provide tenant-specific option sets where a compiled field declares an extensible selection
- Tighten (never loosen) declarative constraints on compiled descriptive fields

## Storage

One `extensions` JSONB column per extensible aggregate root (PostgreSQL):

- Values stored in **canonical wire form** keyed by field key (money/decimals as canonical strings to avoid JSON float damage; dates as ISO strings; references as ids).
- Field keys are **reserved forever**: a retired field's key is never reused, which keeps stored documents, audit history, and integration payloads unambiguous without indirection through ids.
- **Index promotion:** filtering/sorting works out of the box via JSONB expression translation; the registry tracks usage and an admin/developer can promote hot fields to expression indexes (`CREATE INDEX ... ((extensions->>'machineSerialNumber'))`). Index creation is emitted as a reviewable migration/DDL script, not silent runtime DDL — consistent with [14-database-and-ef-core.md](14-database-and-ef-core.md).
- EF Core maps `ExtensionData` via a value converter; the framework provides the query translation layer for extension field access, typed by semantic type.

### Capability tiers

Be honest about what extension fields support, and record it in the manifest:

| Capability | Support |
| --- | --- |
| Display in forms/grids/details/exports | All types |
| Validation & conditional visibility | All types |
| Filter / sort / group | Scalar types (translated JSONB access; promoted index recommended for hot paths) |
| Aggregation in reports | Numeric/money types via typed casts |
| Joins / navigation | Not supported — `Reference` fields resolve display values via batched lookups, not joins |

## Operation pipeline integration

Operations opt in to carrying extension changes:

```csharp
[Operation("orders.edit-details")]
[AcceptsExtensions(For<Order>())]
public static partial class EditOrderDetails
{
    public sealed record Input(
        OrderId OrderId,
        Change<OrderDescription>? Description = null,
        /* ... */) : IExtensibleInput;
}
```

Wire format adds one channel with **identical `Change<T>` semantics**:

```json
{
  "orderId": "order-123",
  "changes": { "description": { "original": "Repair pump", "value": "Replace pump" } },
  "extensions": {
    "machineSerialNumber": { "original": null, "value": "MX-55012" }
  }
}
```

Pipeline behavior for extensible operations:

1. Resolve the tenant's active field definitions (registry revision recorded for audit).
2. Validate extension changes: known key, type-correct, constraints, requiredness, visibility, write permission. Failures are ordinary findings.
3. Run the handler — which normally never sees extension data.
4. Apply extension changes to the aggregate's `ExtensionData` **in the same transaction**, through the same three-way merge with semantic-type equality, producing the same structured conflicts.
5. Emit effects and audit entries per changed extension field (field id + key + label at time of change).

Rules for *which* operations accept extensions:

- Typically the aggregate's `create` and `edit-details` operations declare `AcceptsExtensions`.
- Intent operations (`orders.complete`, `invoices.post`) **never** carry extension changes.
- Handlers cannot branch business decisions on extension values; an analyzer flags reads of `ExtensionData` inside operation handlers (escape hatch: an explicit acknowledgment attribute, reported in the manifest as a reduced guarantee).

Deprecated fields on submit: accepted with a warning finding. Retired fields: rejected. Fields added after the form loaded: absent from the submission, therefore untouched — no error.

## Views, grids, forms, reports

- **Views** opt in: `view.Extensions(x => x.Order)` adds the container to the projection. The framework appends translated JSONB access for selected fields rather than fetching the whole document when only promoted fields are needed.
- **Grid bindings** with `grid.Extensions()` append active extension fields as columns per placement metadata and binding class (web/admin/mobile), after compiled columns by default. Sorting/filtering per the capability tier.
- **Form bindings** with `form.Extensions()` splice extension fields into the declared section/order. Rendering is free: descriptors drive the same generic runtime, renderers keyed by semantic type ([13-frontend-runtime.md](13-frontend-runtime.md)).
- **Reports/exports** are views + bindings, so extension columns flow through identically; numeric fields support aggregation via typed casts.

## Permissions

Extension fields carry per-role read/write permissions, enforced **server-side**:

- Read-denied fields are stripped from view results and descriptors (not merely hidden by the client).
- Write-denied fields reject changes with a findings error.
- The permission catalogue in the manifest merges compiled and extension permissions, so admin tooling sees one model.

## MCP and integrations

- MCP tool/resource schemas generate from the effective manifest per tenant: agents see custom fields with types, constraints, and the admin-authored description. Preflight (`orders.create.resolve`) resolves extension field state exactly like compiled field state. Agents read and write tenant fields through the same operations, findings, and conflicts as humans.
- Integration mappings may target extension fields by field id, with the same ownership/conflict policies as compiled fields. Since compile-time verification is impossible for runtime fields, mapping validity is checked at configuration time and re-checked continuously against the registry (`EXT004`); reconciliation covers extension fields like any other owned field.

## Lifecycle and migration

- **States:** `Draft` (visible only to admins/preview) → `Active` → `Deprecated` (renders read-only, warns on write) → `Retired` (hidden, data preserved, key reserved).
- **No type changes.** Changing a field's semantic type means defining a new field; an optional migration assistant copies/converts values where a safe conversion exists.
- **Requiredness added later** enforces on new writes only; existing rows are grandfathered until an admin runs an explicit backfill task.
- **Renames** change labels only; keys are immutable.
- **Registry changes are operations**, so they are audited, versioned, and revision-bumped; every stored audit entry records the registry revision it was validated against.

## Graduation path

When a tenant field turns out to be consequential — business rules need it, multiple tenants need it, integrations depend on it structurally — it should become code. The framework supports this explicitly:

1. Registry exports the field definition.
2. A scaffolding tool generates the compiled artifacts: semantic value type (or reuse), entity property, EF migration (JSONB → column, with data copy), operation input updates.
3. The compiled field ships; the registry marks the extension field as superseded; the impact report shows every affected boundary.

This keeps the extensibility system honest: it is a staging area for descriptive facts, not a parallel programming model.

## Guardrails (extensibility non-goals)

- No tenant-defined **entities** (v1; revisit only with strong evidence)
- No tenant-defined **operations, workflows, or code** of any kind
- No tenant fields influencing compiled business decisions
- No runtime `ALTER TABLE` / dynamic DDL
- No per-tenant compiled assemblies
- No loosening of compiled constraints per tenant

## Rejected alternatives

| Alternative | Why rejected |
| --- | --- |
| **EAV tables** (row per field value) | Destroys query ergonomics and EF integration; reporting and filtering become joins-on-joins; conflicts with "normal LINQ, normal EF Core". |
| **Runtime `ALTER TABLE` per field** | Real columns and indexes, but unbounded unreviewable DDL in production directly contradicts "migrations must remain reviewable"; schema drift across tenants in a shared database is operationally hostile. |
| **Compile-per-tenant** (fields as generated C#) | Preserves full static typing but turns every field addition into a build+deploy, breaking self-service; deploys become a tenant-count matrix; contradicts the runtime-overlay architecture the plan already commits to. |
| **Open property bag on entities** (`Dictionary<string, object>`) | Explicitly forbidden by the plan, and rightly: no schema authority, no validation, no semantic equality, invisible to every derived representation. The chosen design keeps the bag framework-managed and registry-validated at declared extension points only. |
