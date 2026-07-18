# 22 — Plugins: Packaged Extensibility

**Status: P1–P3 and P5-v1 implemented and verified** (packaging, PLG001–PLG005, per-tenant activation, manifest/MCP/OpenAPI omission, 404 gating, packaged fields on host entities, gates on host operations, effect subscribers, **plugin-shipped inbound integrations** (samples/fortnox), tenant packages with dry-run/atomic install/version guard/retire-on-uninstall, and automation rules as Px-conditioned blocking findings with RUL001–003 — see STATUS.md). Plugins compose with **subscription entitlements** (docs/24): activation is gated by the tenant's plan, which is how a marketplace prices them. **P4 (custom objects) is design; the P5 rule-builder UI is built** — automation rules (conditions over input + `row.*`, relative-date `fn`, the action catalog, effect-triggered rules, and the visual builder over the `rules.schema` view) are all built. One correction from implementation: plugins address host entities and operations by *wire key* (`"order"`, `"orders.complete"`), not CLR types — a plugin references the host's contract, never its assembly. Decision summary: D8 in [19-decisions.md](19-decisions.md).

## The problem this solves

[15-extensibility.md](15-extensibility.md) gives one tenant one custom field. That is the atom of extensibility, not the product of it. A consumer building on Tam — the "Salesforce-like" ambition — needs three larger structures:

1. **A packaged unit of *code* extensibility**: a vertical capability (inspection checklists, time reporting, e-signing) built by the product team or a partner, shipped as a module, switched on per tenant — without the host application knowing it exists.
2. **A packaged unit of *configuration* extensibility**: a bundle of custom fields, roles, and (later) rules that installs into a tenant as one reviewed, versioned act instead of twenty admin clicks.
3. **Tenant-defined *entities*** — custom objects — so a tenant can track something the product never modeled, with forms, grids, permissions, audit, and agents included.

These are three different trust levels, and the design's central move is to refuse to blur them.

## The trust boundary, stated once

| Channel | Who authors | What it is | When it binds | Trust |
| --- | --- | --- | --- | --- |
| **Compiled model** | product team | C# operations/views/entities | build | full |
| **Plugin** | product team / vetted partner | C# module, namespaced | build (of the host), **activated per tenant at runtime** | full — it is reviewed code in the host process |
| **Tenant package** | admin / consultant / marketplace | declarative bundle (fields, roles, rules) | runtime, validated by the registry compiler | data, never code |
| **Custom object** | tenant admin | declarative entity definition | runtime | data, never code |

**Tenants never upload executable code.** That single sentence is what keeps Tam out of the Apex tar pit: no sandbox, no metering VM, no per-tenant compiler service, no security review of customer code. Everything a tenant authors is data validated by a registry (EXT/RUL diagnostics), evaluated by engines we own (the Px AST, the action catalog), and therefore analyzable, auditable, and portable. When a tenant outgrows the declarative ceiling, the answer is the graduation path (docs/15) or a plugin built by someone with a compiler — not a scripting hole.

Salesforce's equivalents, for orientation: plugin ≈ managed package, tenant package ≈ unmanaged package/change set, custom object ≈ custom object, automation rule ≈ validation rule/flow. What we deliberately do not clone is Apex.

Two clarifications the boundary implies rather than forbids:

- **Tenants may run all the code they want — out of process, on their compute, with their credentials.** An external app holds a scoped actor (permissions from the D1 catalogue, nothing more), calls operations and views through the public API, and subscribes to committed effects via webhooks off the outbox. The full pipeline — authorization, validation, audit, idempotency — applies to their code exactly as to a human. This is the Shopify model, and it is the primary answer to "I need custom logic": the marketplace's third tier below.
- **The researched escalation path, if hot-path in-process tenant logic is ever truly required, is a WebAssembly sandbox** (Wasmtime-class: fuel-metered, memory-capped, capability-based — the guest sees only host functions we hand it: read these fields, return findings). Data in, data out; no EF, no reflection, no network. That is the modern Apex, adopted for good reasons by Shopify Functions for their own hot paths. It is named here so the future debate starts from a real design, not from "let's just load their DLL" — which modern .NET cannot isolate at all.

## Plugins (the compiled channel)

A plugin is an ordinary .NET assembly containing the same five concepts as the host — domain state, operations, views, derivations, bindings — plus a manifest class that names it:

```csharp
[TamPlugin("inspect")]                    // the namespace prefix, permanent (wire name rules apply)
public sealed class InspectionPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.RequiresView("orders.detail", "id", "status"); // declared dependency, checked at build (docs/31 D-X3;
                                                          // the earlier RequiresHostEntity<Order> sketch was CLR-shaped and never built)
    }
}
```

The host adds one line — `model.AddPlugin<InspectionPlugin>()` (the source generator emits the discovery, exactly like `AddDiscovered()`) — plus, when the plugin ships its own tables, the storage opt-in in the host DbContext: `InspectionPlugin.AddInspect(modelBuilder)`. Everything else follows from what the plugin's assembly declares.

### What a plugin may contribute

- **Its own entities, operations, views, forms, grids** — indistinguishable from host ones except for the mandatory id prefix: `inspect.checklists.complete`, permission `inspect.checklists.manage`. A plugin id that doesn't prefix all of its ids is a build error (`PLG001`).
- **Packaged fields on host entities.** The plugin declares extension fields on `Order` through the *same* spec the tenant registry uses, but compiled and namespaced: `inspect.requiresInspection`. They ride the existing `ExtensionData` column and `ext.{key}` wire channel; the overlay now has three origins (compiled / plugin / tenant), and key prefixes make collisions impossible. Nothing downstream changes — grids, forms, audit, MCP, filtering already treat extension fields uniformly.
- **Gates on host operations.** The Salesforce-validation-rule power, typed: a plugin may register a precondition on a host operation — a `[Gate("orders.complete")]`-attributed class implementing `IOperationGate` (discovered by `AddDiscovered()` like `[Operation]`/`[View]`; the fluent `plugin.Gate<T>(...)` remains the substrate), constructed per invocation with ctor injection, returning findings. The pipeline runs gates after authorization, before the handler. Every gate is listed in the manifest and the impact report (`orders.complete` shows "gated by inspect"), so the coupling is visible, not magic (`PLG002`: a gate on a nonexistent or non-deterministic target is a build error).
- **Effect subscribers.** Plugins react to committed host effects (the outbox) — never by patching host handlers. "When an order completes, open an inspection" is a subscriber creating its own aggregate via its own operation, with its own audit trail.
- **Inbound integrations.** A plugin ships an external-system integration targeting a host operation by wire id (`plugin.Integration("orders.import", "orders.create", key, map)`), mapped to `POST /api/integrations/{plugin}.{id}`, activation-gated, inbox-idempotent with retry and late-fix recovery. The mapper resolves external identities through host *views* as the actor — never host tables. Implemented: samples/fortnox.
- **Locales and permissions**, merged like the system module's are today: plugin defaults embedded, app files override, L10N001 gates coverage.

### What a plugin may NOT do

Reach into another module's `DbContext`, replace host operations, contribute middleware, or register un-namespaced anything. Plugins compose *around* the host's model through the same public seams every other consumer uses (operations, views, effects, extensions). If a plugin needs host data, it queries host views with the actor's permissions — a plugin is not a superuser.

### Activation is tenant data

The plugin is compiled into the deployment, but it is *dormant* until a tenant activates it (`plugins.activate`, a framework operation like `extensions.define-field` — audited, revisioned, permission-gated). The effective manifest simply omits contributions of inactive plugins: no nav, no operations, no MCP tools, no packaged fields. HTTP calls to an inactive plugin's operations 404 per tenant. This gives the "install an app in your org" experience with build-time safety — installing new *code* is a deploy of the host; *enabling* it is a click.

Per-plugin entitlement (billing tiers) is the same mechanism with a different guard on the activate operation.

### Versioning and compatibility

D4 applies per plugin: each plugin's manifest contribution is baseline-checked in the host's CI, and its wire names/permissions are permanent. A plugin upgrade that removes an operation fails the host build until the baseline is consciously re-approved — the host vendor, not the tenant, absorbs compatibility risk, which is where the compiler is.

### Framework packages (the framework-trust tier) — BUILT

The framework's own admin capabilities register through the SAME `PluginBuilder` surface a
vendor plugin uses — `AddTamSystem()` is fifteen `[TamPackage]` modules (`tam.users`,
`tam.audit`, `tam.roles`, `tam.tenancy`, `tam.rules`, `tam.documents`
([36-documents.md](36-documents.md)), `tam.developer`, …), each shipping its operations, views,
forms and grids. Every framework capability therefore exercises the plugin seams daily — the
strongest regression guard the seams can have. A package differs from a vendor plugin on the
tier axes only:

- **Always active.** Never in the activation table, never entitlement-gated; every activation
  consumer (pipeline existence check, gate filter, manifest, outbox dispatch) unions package
  ids in. There is nothing to toggle — `plugins.activate` (input: `{ "pluginId": ... }`) doesn't know packages exist, so the
  always-on tier can't be switched off (who activates the activator).
- **Claimed prefixes instead of an id namespace.** Framework wire names (`users.invite`,
  `audit.entries`) are live and permanent (D4), so a package CLAIMS the prefixes it has always
  owned — `[TamPackage("tam.users", "users", "web.users")]` — and PLG001's package variant
  validates every contribution against the claims.
- **Framework trust.** Packages ship in the Tam repo and may touch framework tables; the hook
  set itself (gates, subscribers, park, replay) stays closed even for packages — the executor
  remains auditable. Middleware/endpoint access, when it arrives, is package-tier only: a raw
  endpoint bypasses the pipeline, which is exactly the hole the wire-only rule closes for
  vendor plugins.

What stays CORE, never a package: authorization and actor resolution (a gate fails OPEN by
absence — nothing mandatory can be a gate), tenant isolation, the same-transaction pipeline
atoms (idempotency, audit capture, outbox write, extension channel), entitlement/seat
enforcement, and the activation machinery itself.

## Tenant packages (the declarative channel, bundled)

Everything the tenant registry accepts one item at a time — extension fields today; roles; later custom objects and rules — can be expressed as one JSON document and installed as one act:

```jsonc
{ "package": "cold-chain", "version": 3,
  "fields":  [{ "entity": "order", "key": "coldChainClass", "type": "choice", … }],
  "roles":   [{ "name": "cold-chain-auditor", "grants": ["orders.read", "audit.read"] }] }
```

Install runs the exact registry validation each item would get individually (EXT005/EXT006, roles' catalogue check), atomically — all findings or all applied, with a dry-run mode that returns the findings without applying. Uninstall retires (never deletes — data outlives configuration, as with field retirement). Upgrade is a diff against the installed version with the same additive bias as D4: narrowing a choice list or retiring a field that has data produces warnings that require explicit confirmation.

A tenant package is a *file*: it lives in a repo, gets code review, installs into the test tenant first, and a consultant can carry the same package to ten customers. This is deliberately the boring 80% of "AppExchange" — distribution and billing are product features, not framework ones; the framework's job is that installation is validated, atomic, audited, and reversible.

## Custom objects (the tenant-defined entity)

> Superseded in detail by [23-custom-objects.md](23-custom-objects.md), which resolves the permission model and storage reuse; the sketch below records the original direction.

The largest step, and the reason the extension registry was built as a compiler from day one. A custom object definition is registry data:

```jsonc
{ "object": "coolingUnit",
  "labels": { "sv": "Kylaggregat", "en": "Cooling unit" },
  "fields": [ { "key": "serialNumber", "type": "text", "required": true },
              { "key": "installedAt", "type": "date" },
              { "key": "order", "type": "reference", "target": "order" } ],
  "titleField": "serialNumber" }
```

From it the framework derives what it derives for compiled entities: standard operations (`custom.coolingUnit.create/edit/list` — generated, but flowing through the *same* pipeline: authorization, validation, transaction, audit, idempotency, effects), permissions (`custom.coolingUnit.read/create/edit` entering the catalogue for roles to grant), manifest surface (forms, grids, MCP tools — agents see a custom object exactly as they see orders), and filtering/sorting via the mechanical D7 path.

Storage is one shared table (`custom_records`: TenantId, ObjectKey, Id, Version, Document jsonb, plus the audit tables it already shares) — the JSONB decision (docs/15) generalized from a column to a document. References are validated at write time; reverse lookups ("orders referencing this cooling unit") are a derived view.

**Prerequisites before this ships**, in dependency order: numeric/typed JSON predicates (today's extension filtering is string containment), promoted expression indexes (docs/15's performance path), and D2's RLS backstop. Custom objects inherit the graduation path: when `coolingUnit` becomes load-bearing, it graduates to a compiled entity and the registry entry becomes a redirect.

**Ceilings, stated honestly:** no tenant-defined invariants beyond field constraints and automation rules (below) — a custom object with real business rules is the signal to graduate; no cross-object transactions authored by tenants; list performance is bounded by JSONB until an index is promoted.

## Automation rules (declarative logic, Px-bounded)

The last Salesforce pillar, and the one where the "no tenant code" line matters most. A rule is: **trigger** (an operation) + **condition** (a Px AST tree — the same portable expression language forms already evaluate) + **action from a closed catalog (BUILT)**: the blocking **finding** (default), **set-field** (write a registered extension field on the operation's target row), **publish-event** (an outbox row with the derived type `rules.{name}`). "Enqueue an integration message" is COMPOSITION, not a fourth action: publish the event and point an event-triggered outbound integration (docs/25) at `rules.{name}` — retries, dead-letter and audit come with the channel. Phases keep the physics honest: finding rules stay the PURE pre-transaction gate (cheap fail); action rules run as a second, TRANSACTIONAL gate — set-field rides the tracked row into the operation's own SaveChanges (one commit, audited on the operation's entry), publish-event lands in the same commit and dispatches like any event. Actions never block; a rule that cannot evaluate warns. RUL005 (`rules.invalid-action`) validates the action as data at define time: only the closed types; set-field only against the target entity's REGISTERED, WRITABLE, active extension fields with a value that passes the field's own semantic type + options — the *same* checks the wire channel and the packaged-field writer enforce, applied at define AND re-checked at execute so a rule can never outlive a field's constraints (a now-invalid set-field degrades to the non-blocking `rules.evaluation-failed` warning, never a silent bad write); ReadOnly (plugin-owned) fields are refused (compiled/plugin state stays behind intents — EDIT001 extended to tenants). publish-event's type is *always* `rules.{name}` — a prefix RESERVED at build (no package may declare a `rules.*` event), so a rule's event can never coincide with a compiled contract, now or after a later deployment.

One honest limitation: a set-field write lands on the operation's own audit entry, attributed to the ACTING user — the rule's causation is not stamped on that entry (the packaged-field writer, by contrast, writes a separate plugin-attributed audit). Because every rule DEFINITION is itself audited under `rules.manage`, the causal chain is reconstructable, but per-write rule provenance on the audit entry is a tracked follow-up, not yet built.

The REAL wire (RTFM #3 corrected this block — the earlier sketch shape is rejected):
`rules.define` takes `{ name, onOperation, condition, messages, targetField? }` where
`condition` is the Px tree as a JSON **string** (structured data in a string field — the
engine deserializes and validates it; it is never expression-parsed) and `messages` is
per-culture rule text keyed like the catalogs. Px nodes carry discriminator `t`:

```jsonc
// "cold-chain orders need a requested date", as rules.define's condition string:
{ "t": "bin", "op": "and",
  "l": { "t": "bin", "op": "eq",
         "l": { "t": "field", "f": "ext.coldChainClass" }, "r": { "t": "const", "v": "class-2" } },
  "r": { "t": "bin", "op": "eq",
         "l": { "t": "field", "f": "requestedDate" }, "r": { "t": "const", "v": null } } }
```

The CLOSED operator vocabulary (anything else is `rules.invalid-condition`, which names the
offending `op`): binary `eq ne gt ge lt le and or`, unary `not isNull isNotNull`; leaves are
`{"t":"field","f":...}` (input wire names, `ext.{key}`, `row.{member}`, `row.ext.{key}`) and
`{"t":"const","v":...}`, and the FUNCTION node `{"t":"fn","op":"today","days":N}` — today's
UTC date (+offset) as the ISO string dates compare in, evaluated FRESH on every check on
both evaluators (RTFM #3's define-time-constant drift, closed; the erp seed's
urgent-schedule-window rule is the living example). A firing rule's finding code is
**`rules.{name}`** — what tests and clients match on. Seeding a rule for a demo tenant writes the same
`AutomationRuleEntity` row the operation writes (`ConditionJson`, `MessagesJson`, and for
row conditions the define-resolved `RowEntityKey`/`RowIdField`) — the erp seed shows the
shape.

Rules are registry data (RUL### diagnostics at definition: unknown field, type mismatch, unreachable condition), evaluated with a budget (no loops, no recursion, bounded tree depth), fully audited, and — the sharpest dogfood in the codebase — **executed as the `tam.rules` package's own PURE wildcard gate**: the executor has no rules special case; the framework's P5 feature runs through the very gate seam it sells, in the pre-transaction phase (a declared pure-over-input gate is the cheap fail; transactional gates keep running inside the transaction). And — because Px is portable — the *same* condition can drive client-side form behavior without a round trip. Message keys, as always: the finding's text lives in the tenant's label catalog, per culture.

What rules never get: arbitrary code, HTTP calls (that's the integration channel, via the enqueue action), or writes to compiled fields (operations own compiled state transitions — EDIT001's philosophy extended to tenants).

### The `row.*` increment (BUILT — the P5 slice the field-service arc queued)

v1 conditions see the **operation input only**. That covers create/edit operations (rich
inputs) but nearly nothing on INTENT operations — EDIT001 makes intents deliberately thin
(`orders.complete` carries an id), so the very operations tenants most want policy on are
the ones an input-only rule cannot inspect. The field-service arc hit this wall
(docs/34 M6): "service orders can't complete without an approved checklist" is not
expressible over `{ orderId }`.

The bounded fix, as shipped: a second Px namespace, `row.*`, resolved by the rule gate
from the operation's TARGET row. Boundaries that keep Px an expression language and not a
query language:

- **One row**: the target resolves at DEFINE time from the operation's single `{entity}Id`
  input (`projects.close` + `projectId` → the project row); zero or several candidate id
  inputs is RUL004 (`rules.no-target-row`) — creates and bulk operations simply don't
  offer `row.*`, named at define, never hit at runtime. No joins, no aggregates, no
  navigation ("all checklist items done" stays plugin-gate CODE).
- **RUL002 extends over the row**: `row.{member}` verifies against the entity's members
  and `row.ext.{key}` against the tenant's extension registry, at definition time.
- **Wire-identical semantics**: the hydrated row is serialized through the platform's
  JSON options before Px sees it — enums compare as their wire strings, `Tam.Money` as a
  plain number, wrappers unwrap — so a row condition reads exactly like an input one.
- **Fail-safe shape**: the row loads read-only pre-transaction, tenant-checked explicitly
  (FindAsync bypasses the global filter — the packaged-writer lesson applied); a MISSING
  row means the rule does not fire (the pipeline's own not-found follows), and only a rule
  that cannot EVALUATE emits the non-blocking `rules.evaluation-failed` warning.
- Client-side parity: the visual builder (below) resolves `row.*` fields server-side via
  the `rules.schema` view — the browser has no row, so it asks rather than pretends.

Why this ordering: the action catalog widens what rules can DO; `row.*` widens what they
can SEE — and the arc showed the seeing gap binds first. Verified: 4 evaluator unit tests
(fire/quiet/missing-row/tenant-boundary over real Money + enum members) and 6 wire checks
in the rules suite (define-resolve, block, pass, RUL004, RUL002-over-row, retire).

### The visual rule builder (BUILT — the P5 UI slice)

Authoring a rule as raw Px JSON is fine for the platform team and impossible for an
admin. The builder replaces the JSON textareas on `rules.define` with a **trigger picker →
typed condition clauses → set-field/finding/publish-event action**, all rendered from
data the *server* owns — the "BE Form way": the client contributes pixels, never field
semantics.

The one thing the manifest cannot supply is the target ROW's field types (compiled entity
properties are not in the manifest, only operation/view field descriptors), so a small
computed view fills exactly that gap:

- **`rules.schema` view** (`?trigger={id}&kind=operation|event`, behind `rules.manage`):
  returns one row per referenceable compiled row field — `{ path, labelKey, wireKind,
  options, entityKey }` — resolved through the SAME `FieldModel` path that types operation
  fields, so `row.status` arrives as a `string` carrying its enum options and `row.budget`
  as a `number`. It mirrors RUL004: a trigger with no single `{entity}Id` returns nothing,
  so the builder offers neither `row.*` nor set-field for it. Pure and synchronous — the
  extension fields (`ext.*`, `row.ext.*`) come from the manifest overlay the client already
  holds, keeping tenant typing where it lives and the view free of the async registry read.
- **The client assembles**, it does not re-derive: `conditionRefs(manifest, schema, …)`
  unions the trigger's input/payload fields, its extension fields, and the schema's row
  fields; `operatorsFor(wireKind)` picks the operator list; the value control is chosen by
  `wireKind`/`options` (enum → localized select, number → numeric, date → a specific date
  **or** the relative `fn` "today ± N days", boolean → true/false). All of this is
  server-authoritative typing — the client only maps a wireKind to a widget.
- **Round-trips to Px, losslessly**: the clause model serializes to the same portable AST
  the evaluator runs (`buildCondition`/`buildAction`), and `parseCondition` reads it back;
  a hand-authored condition that does not fit the flat all/any-of-clauses shape — or does
  not parse at all — drops to a raw-JSON "Advanced" editor rather than silently losing
  structure. Honest limitation: event payload fields are declared as NAMES only, so an
  event trigger's top-level payload fields default to string/equality while its `row.*`
  fields get full typing.
- **The form dogfoods its own dynamics** (docs/05): `rules.define` is an ordinary form
  whose builder fields are gated with the same `VisibleWhen`/`RequiredWhen` Px every other
  form uses — Condition/Messages/Action appear once a trigger is chosen, TargetField only
  for finding rules (it is the finding's anchor), and Messages flips required exactly when
  RUL003 will demand it (no action). TargetField's options come from a **server
  derivation** (`rules.define.target-fields`, recomputed on trigger change through the
  resolve endpoint), so the finding's anchor is picked from the trigger's own localized
  fields, never typed. "Exactly one trigger" and "authoring resets on trigger change" are
  both the declarative `ResetOn` (docs/05) — the pickers themselves are plain searchable
  selects with no sibling knowledge. And a comparison against the null CONSTANT (the
  unfinished-clause shape) is refused at define with a redirect to `isNull`/`isNotNull`,
  mirrored by an inline "value required" hint in the builder.
- **Rules are editable in place**: `rules.define` is an upsert by name, and the grid's
  `RowForm` (docs/32) opens it PREFILLED from the row — `rules.list`'s result record
  deliberately carries the full definition (condition, messages, action, trigger) so the
  stored Px parses straight back into the visual clauses, including the relative-date `fn`
  node as its "today ± N" control.

Verified: 3 backend view tests (typed row fields, empty-for-creates, unknown-trigger) and
a 8-check wire suite on SQLite **and** Postgres (schema shape, enum options, the excluded
extension bag, the event target row), plus a UI screenshot of the populated builder with a
`row.status` value control offering its localized `Öppen`/`Avslutat` options.

## Where a feature goes: package vs plugin, one vs many

The tiers above encode this implicitly; these are the judgment calls, made repeatable
(distilled from the field-service arc — the checklist feature is the worked example).

**Package vs plugin is a trust question, not a size question.** It is a framework package
when any of: it needs framework internals or framework-level trust (identity tables, the
gate evaluator itself); the platform's own story breaks without it (users, roles); or it is
domain-agnostic — every host wants it regardless of business. It is a plugin when the
opposite holds on all three: domain-opinionated, optional per tenant, composed entirely
through the public contract (wire keys, events, views — never the host's assembly). The
operational tell: **if it needs per-tenant activation and a price, it is a plugin; if
"activating it per tenant" makes no sense, it is a package.**

**One plugin vs many is a contract question**, tested in order:

1. **One reason to activate.** A plugin answers a single tenant decision ("do we do
   inspections?"). If a tenant plausibly wants A without B, they are two plugins — inspect
   and invoicing were never one "orders extras" plugin.
2. **How would the halves touch?** Sharing entities/tables → one plugin. Touching only
   through events and declared views (the way invoicing consumes time/materials) → they can
   stay separate. Shared tables are a marriage; shared contracts are an acquaintance.
3. **The entitlement line is a real line** (docs/24): if the pricing page lists them
   separately, the code does too — activation gating is per plugin.
4. **Uninstall must be definable.** "Retire this plugin" must leave an audit-coherent
   state; if uninstalling half a feature is meaningless, it is one plugin.

**The rule that keeps granularity honest: plugins depend on the HOST's contract, never on
each other.** The moment two plugins genuinely need each other, merge them or promote the
shared concept down into the host (work-order-completed became a host event both invoicing
and inspect consume). Plugin-to-plugin contracts are a deliberately unbuilt seam — that is
where marketplace ecosystems grow their dependency hell, and this line is cheaper to hold
than to win back. **ENFORCED: PLG010** — a plugin's `RequiresView`/`RequiresEvent`/
`OnEffect` target must be owned by the host, by a framework package (always-on, host-like
trust), or by the plugin itself; anything else fails the build. Until PLG010 the rule was
doctrine only — the checks verified existence and fields, not ownership.

### "What can I extend?" — the host contract artifact

A plugin author's first question has one answer: the host's exported
**`host-contract.json`** (docs/31 slice 3) — every consumable event (fields, kinds), view
(fields, kinds, permission), slot (panel targets + context keys), extensible entity
(packaged-field targets) and gateable operation, in one versioned file. Reference it as an
`AdditionalFiles` item and the source generator turns the whole surface into IntelliSense:
`HostContract.Events.*` / `HostContract.Views.*` for requirements, plus a typed facade per
event and view — and requirement declarations go THROUGH the facades:
`RequiresEvent<OrderCompletedEvent>()` (fields and kinds from the record),
`RequiresView<OrdersDetailRow>(r => r.Id, r => r.Number)` (the subset selected by lambda —
no field strings anywhere). Update the host dependency by replacing the file; every rename
becomes a compile error (D4 keeps snapshots stable: wire names are permanent). The same
surface renders IN the running app: the `tam.developer` package serves the contract through
the `developer.contract` view (permission `developer.read`), and the host places the
developer PORTAL page in its own nav mode — artifact, IntelliSense and portal are three
synchronized forms of one contract.

### Plugin-on-plugin: the tier that isn't (yet)

If a real extension ecosystem arrives ("a photos plugin extending inspect's checklists"),
the beautiful property is that almost NOTHING new is needed on the contract side — the
host was only ever the FIRST provider. Every plugin's contribution is already a manifest
slice with declared events, views and kinds; the contract-artifact mechanism (docs/31)
generalizes verbatim: an extender compiles against inspect's exported contract exactly the
way any plugin compiles against the host's, and composition Build() still verifies against
the real co-installed set (D4 additivity tames the version diamond: contracts only grow).

What IS genuinely new — and why the line stays until it is designed deliberately — is
**existence semantics**, not contracts:

- a declared dependency edge (`DependsOn("inspect")`, derivable from requirement targets'
  owners) with activation ORDER: the extender is activatable only where the base is
  active, and deactivating the base cascades or suspends the extender — today's
  activation model has no conditional existence between plugins;
- the entitlement coupling (docs/24): the pricing page cannot sell the extender without
  the base, and uninstall coherence must hold across the edge;
- UI extension points: PLG005 would relax to "a plugin may declare slots on its OWN pages
  and records" (never the host's — layout stays the host's), so extenders contribute
  panels through the existing PLG007 machinery;
- extensible plugin entities: a plugin's entity implementing `IExtensible` joins the same
  extension-field seam tenants already use.

When the need is real, lifting PLG010 for a DECLARED dependency edge is the design
commit — the gate is the placeholder for that decision, not a refusal of it.

## The marketplace: three tiers, one trust model

"Pick and choose from a marketplace" needs no tenant code upload — it decomposes onto the channels above:

| Tier | Listing | Install | Contained by |
| --- | --- | --- | --- |
| **Configurations** | tenant packages (fields, roles, rules) | self-service: dry-run → findings → atomic apply | the registry compiler |
| **Vetted plugins** | compiled partner modules | vendor reviews + builds in; tenant clicks *Activate* | build-time diagnostics + review |
| **External apps** | third-party services | tenant grants the app a scoped actor; app uses API + webhooks | D1 permissions |

Catalog UI, ratings, and billing are product features on top of `plugins.activate` and package install; the framework owes the marketplace only what D8 already defines — namespacing, per-tenant activation, entitlement guards, atomic validated install, scoped app actors (arrives with real authn).

What the mechanics make possible that incumbent marketplaces get only from manual review: **a provable capability manifest per listing**. A tenant package's exact footprint — which entities it touches, which fields it adds, which operations its rules gate, which events it consumes — is *derived from its content by the registry compiler*, not claimed by its author. A plugin's footprint (contributed operations, packaged fields, gates, subscribers) is likewise a build artifact, and a plugin *upgrade* diffs as a manifest diff. The install screen can therefore say "this package reads orders, writes `ext.coldChainClass`, gates `orders.complete`, and never sees customer emails" with compiler authority. Shopify approximates this with coarse OAuth scopes; Salesforce with human security review. Here it falls out of contributions-as-data.

A tenant's entire customization — installed packages, fields, roles, rules, custom objects — is likewise one serializable, diffable document: **the org is a file**. Test-to-production promotion is a diff review; migrating a tenant is an export; the "what changed in our org this quarter" question is version control, not archaeology.

## Beyond parity: what the model enables that the incumbents can't retrofit

Everything above reaches parity with Salesforce/Shopify on safer foundations. These four are the moves that go *past* them — each is possible only because of commitments already made (manifest as the single contract, operations as the only write path, Px as structured expressions, same-transaction audit), which is precisely why they are hard to copy. Design-stage, ranked by leverage:

1. **Agent-authored customization.** Both incumbents built human-first admin UIs and are now bolting agents onto metadata swamps. Tam's effective manifest *is* an agent's world model — typed, per-tenant, complete — and the registry's diagnostics are the agent's guardrails: it structurally cannot install an invalid package, and dry-run gives it a feedback loop. The endgame: a tenant admin says "we need cold-chain tracking on orders with an auditor role and a rule that class-2 requires a date", and the agent authors the tenant package, dry-runs it, presents the findings and the capability manifest, and installs on approval — through `extensions.define-field`-class operations that already exist and are already MCP tools. Extensibility-by-conversation with compiler-checked output is a different product category than "AI helps you click".
2. **Time-machine dry-run (replay impact preview).** Because operations are the only mutation path and the audit trail stores every invocation in canonical wire form, a candidate rule/package can be evaluated against *recorded reality*, not a guess: replay the tenant's last N weeks of audited operations through the pipeline with the candidate configuration overlaid, inside transactions that never commit and with effects suppressed, and report "this rule would have blocked 37 of 412 real orders — these three users hit it most". Advisory (time-dependent logic and sequences make replay approximate), but it converts the scariest moment of tenant self-service — "what will this break?" — into evidence. Salesforce cannot do this generally (Apex side effects, no uniform operation log); it falls out of Tam's audit + purity decisions.
3. **Deterministic composition with provenance.** The moment two packages and a plugin all touch `orders.complete`, incumbents descend into undefined trigger order and flow-vs-trigger folklore. Tam can compile each tenant's full contribution set (gates, rules, subscribers — all data or declared code) into one ordered, cycle-checked execution plan at install time: two writers to one extension field is an *install-time finding*, not a production mystery; and every runtime finding carries the rule/package/plugin that produced it, so "why was this blocked?" has a provenance answer in the UI. The effective manifest already merges origins; this extends the merge from fields to behavior.
4. **The binding-time dial (graduation as tooling, both directions).** For the incumbents, clicks and code are different universes with no path between them — orgs rot into hundreds of flows nobody dares touch. Tam's thesis is that a custom object and a compiled entity are *the same model at different binding times*, so graduation becomes a command: scaffold the C# entity/operations from the registry definition, generate the data migration, preserve wire names so no client notices. And the dial turns both ways — a compiled feature that only one tenant uses can be demoted to a package. Nobody offers configuration→code→configuration mobility; the wire-name permanence rule (D4) is what makes it invisible to callers.

Ordering note: #2 needs only machinery that exists today (audit + pipeline + overlay), #1 needs P3's dry-run install plus the MCP tools that already exist, #3 becomes real at P5, #4 at P4. None create new trust surface — they are leverage on the boundary, not holes in it.

## Phasing

1. **P1 — plugin packaging**: `ITamPlugin`, id namespacing + PLG001, discovery via the source generator, per-tenant activation, manifest omission for inactive plugins. Proves the seam with a real sample plugin.
2. **P2 — plugin depth**: packaged fields on host entities (three-origin overlay), operation gates + manifest/impact visibility, effect subscribers.
3. **P3 — tenant packages**: bundle format, dry-run install, atomic apply, retire-on-uninstall, upgrade diffs.
4. **P4 — custom objects**: after typed JSON predicates + index promotion + RLS. Generated standard operations through the real pipeline.
5. **P5 — automation rules**: Px rule storage, RUL diagnostics, action catalog, pipeline evaluation with budgets.

Each phase is independently shippable and independently valuable; nothing in P1–P3 waits on the JSONB query work that gates P4.

### Effect-triggered rules (BUILT — the last P5 rules slice)

An operation-triggered rule evaluates in the pipeline and its action rides the operation's own
transaction. An EVENT-triggered rule — "when an order is created, flag project-type orders for
review" — evaluates on the outbox dispatch path where plugin subscribers already run
(docs/09-10). As shipped:

- **Trigger** is `onEvent` (a declared DOMAIN event) instead of `onOperation`; exactly one is
  set (`onOperation` is now optional). `rules.*` events are refused as triggers (RUL006).
- **Condition** reads the event PAYLOAD (its declared fields) and, via `row.*`, the entity the
  payload references by a `{entity}Id` field — the same row machinery, sourced from the
  payload instead of an operation input (RUL004 still names a payload with no single target).
- **Action is set-field ONLY** (RUL007 forbids publish-event from an effect rule; a
  post-commit finding blocks nothing, so a set-field is required). This is the load-bearing
  safety constraint: no-publish-event **plus** no-`rules.*`-trigger makes a rule → event → rule
  cycle structurally impossible. The write goes through the same `RejectSetFieldValue` guard
  (ReadOnly/state/semantic/options) and the same tenant-checked row load as operation rules,
  and rides the dispatcher's per-record `SaveChanges` — isolated like a plugin subscriber, so
  a broken rule never wedges dispatch.

Built with the round-5 review's findings applied as it was written (the dispatcher is a
multi-instance, lease-based hot path; tenant-data writes there are exactly what round 5 showed
can hide bugs). Verified: 3 harness tests + 4 wire checks proving the field is set on the
referenced row when the event dispatches, on SQLite and Postgres.


## The plugin authoring reference (what the RTFM run proved must be written down)

Two documentation-only implementers built plugins against this chapter; everything below is
what they had to discover through compiler errors. Normative from now on:

- **Data access**: plugin operations/views/gates take **`ITamDb`** (namespace `Tam.EntityFrameworkCore`;
  surface `tam.Db.Set<T>()`) as an ordinary handler parameter — never a host DbContext type.
  Host reads go through **`IHostViewReader`**: actor mode
  `RowsAsync(viewId, IReadOnlyDictionary<string, string?> query, OperationContext, ct)`
  (values are wire strings — `guid.ToString()` yourself), service mode drops the context and is
  whitelisted by `RequiresView`. Both return `ViewResponse` (`Rows`, `Total`).
- **Entities**: tenant scoping is structural — implement
  `Tam.EntityFrameworkCore.ITenantScoped` (a settable `string TenantId`); the ambient filter
  and the insert stamp then apply. Store timestamps you intend to SORT on as ISO-8601 strings
  (the framework's `*Iso` convention — SQLite cannot order `DateTimeOffset`); `IsoTime.Now()`
  produces them. Optimistic concurrency (`Version`) is opt-in via `IVersioned`.
- **Bindings and events**: a plugin declares its own forms/grids with
  `plugin.Form<T>(...)` / `.Grid<T>(...)` (convention defaults apply, docs/32 D-P6) and
  its events with `plugin.PublishesEvent("id.event", ...fields)`; an operation publishes
  with `result.Effect(new EventPublished("id.event", payload))`. Binding/nav/view/operation ids
  all sit under the plugin prefix — for web bindings that means **plugin first, surface second**
  (`invoicing.web.invoices`), the mirror of the host's `web.orders.list` (PLG001 enforces it).
- **Locales**: ship `locales/{sv,en}.json` as `EmbeddedResource` — AddPlugin/AddPackage
  loads them automatically as defaults (application locale files override).
  Required keys beyond your fields: `plugins.{id}.title`, and
  `ext.{id}.{key}` for every packaged extension field. Every label key — inputs, OUTPUTS,
  results, nav — is L10N-gated in the default culture at Build().
- **Cross-module options**: a plugin field that stores another module's vocabulary stays a
  wire string — and offers that vocabulary as options with
  `form.Field(x => x.OrderType).EnumOptions("order-type")` (the enum's kebab wire name,
  resolved from the model's enum registry, ENUM001-verified at Build). No CLR coupling, no
  free-text typos.
- **Slots take multiple panels**: one plugin may contribute several `plugin.Panel(...)` to
  the same slot — they render in contribution order (docs/31).
- **Testing** (Step 11): activation is testable through the front door — seed a
  `SubscriptionEntity` whose `EntitlementsJson` includes your plugin id, then execute
  `plugins.activate`. Subscriber effects fire when the test calls
  `host.DispatchOutboxAsync()`.
- **csproj**: copy a sample plugin's project file — the contract is references to
  Tam.Core/Tam.EntityFrameworkCore/Tam.AspNetCore, `Tam.Compiler` as
  `OutputItemType="Analyzer"`, `AdditionalFiles` + `EmbeddedResource` for `locales/*.json`.
  Build()-time PLG/L10N checks run in the HOST build — a plugin compiling green alone has not
  been verified until a host registers it (the manifest export is the cheapest host check).
