# 31 — Cross-domain plugins: extending a domain you don't own

Status: **BUILT** — phase 1 (D-X1 grid actions, D-X2 field writer, D-X3 RequiresView + reader,
`samples/invoicing`, tutorial Step 17) and phase 2 (D-X4 slots + `<PluginSlot>`, D-X5 event
contracts + PLG009). Remaining follow-ups: host-action migration to declared binds; action/panel
suppression via the tenant-override registry; and a KNOWN WRINKLE — packaged fields ride the
extension form channel, so a host edit form renders a plugin-owned field as user-editable
(invoiceStatus is a plugin state machine; a `readOnly` flag on packaged specs is the fix).
Decisions D-X1…D-X6. The driving case is an **Invoicing
plugin that becomes part of the host's Orders domain** — create-invoice from the orders grid,
invoice status on the order row, drafts written by order events — without the plugin ever
referencing a host CLR type, and without the host ever naming the plugin. Step 17 of the
tutorial builds it.

## The bar

Steps 13/16 set it: *the host adds one line (`AddPlugin<T>` + the storage opt-in); the domain
never notices.* An audit against the invoicing case shows the back half of the story already
clears that bar with existing seams, and the front half — the plugin appearing **inside the
host's own surfaces** — does not. What exists, mapped:

| Invoicing needs | Seam | Status |
| --- | --- | --- |
| Invoice/InvoiceLine entities in the host DB | plugin entities + host `AddInvoicing(modelBuilder)` (inspect/approvals shape) | works today |
| Operations keyed by order id | plain `Guid OrderId` input — ids are wire data | works today |
| Draft invoice when an order completes | `OnEffect("order-completed")`, post-commit, tenant-pinned, activation-filtered | works today |
| Block `orders.complete` until invoiced | `plugin.Gate<T>("orders.complete")` — the one in-transaction seam | works today |
| `invoiceStatus` column/filter on the host's orders grid | `plugin.ExtensionField("order", …)` — READ side is mechanical | declare+read today; **no writer** (D-X2) |
| Invoices page in nav | `plugin.Nav(…)` contribution | works today |
| Push invoices to the accounting provider; poll payments | outbound integrations (fortnox shape) | works today |
| Priced, per-tenant switchable | entitlement gate + activation + manifest omission | works today |
| "Create invoice" ON the host's orders grid | — | **gap → D-X1** |
| Validate "this order exists, is completed, is mine to see" | fortnox casts ViewExecutor out of DI — unblessed, undeclared | **gap → D-X3** |
| Invoice panel on the order detail | — | deferred → D-X4 |
| `order-completed` payload shape as a contract | stringly, unchecked | deferred → D-X5 |

The two REQUIRED seams are D-X1 and D-X2: without the grid action the user must navigate to a
plugin page and hand-enter an order id; without the field writer the plugin can declare
`invoiceStatus` but never set it — a column that is a lie. D-X3's declaration half is required
for honesty (the read dependency should be a build-time fact, not folklore); its runtime facade
is formalization of what fortnox already does.

## D-X1 — Grid action contributions

```csharp
plugin.GridAction("web.orders.list", "invoicing.create-from-order",
    bind => bind.Field("orderId", fromColumn: "id"));
```

A plugin attaches **its own operation** as a row action on a **host grid**, with a DECLARED
input↔column binding — wire names on both sides. Placement mirrors nav (D-N2): the plugin says
"this action belongs on that grid"; activation decides whether the tenant sees it; permission
decides whether the user does.

- **Model**: `GridActionContribution(GridId, OperationId, PluginId, IReadOnlyList<(Input, Column)> Bind)`,
  collected beside gates on the builder — a `PluginBuilder`-only seam (PLG005 pattern).
- **Build() — PLG006**: the target grid exists; the operation is the CONTRIBUTING plugin's own
  (a plugin never re-points host actions or another plugin's); every bound input field exists on
  the operation; every bound column exists on the target grid's view Result; duplicate
  (grid, operation) rejected like PLG002.
- **Manifest** (unified in the beauty arc): every grid affordance — host RowAction/RowForm/
  ToolbarAction AND plugin contributions — is ONE descriptor in `ManifestGrid.actions`:
  `{operation, placement: row|toolbar, mode: execute|form, bind?, plugin?}`. A contribution is
  the same descriptor carrying a bind (input ← row column) and the owning plugin id, merged per
  tenant exactly like `GatedBy`. The client renders one list through one permission/existence
  filter; a declared bind replaces the name-convention input mapping wherever present.
- **No veto in v1** (owner call): activation is the tenant's switch and permissions are the
  user's — a tenant that doesn't want the button doesn't activate the plugin. A host-level
  `Suppress(pluginId)` and per-tenant hiding ride the nav-override registry pattern later,
  additively. The marketplace capability line — "adds an action to your orders list" — is
  derived from the model, not claimed.

## D-X2 — The packaged-field writer

```csharp
// ctor-injected into gates / effect handlers / the plugin's own operations:
await fields.SetAsync("order", orderId, "invoiceStatus", "invoiced", ct);
```

The missing write half of P2's packaged fields. `IPackagedFieldWriter` is constructed
**plugin-scoped** by `TamActivator` (the activator already knows the owning plugin id from the
gate/subscriber definition), so enforcement is structural, not policed:

- only keys under the calling plugin's prefix, only on (entity, field) pairs that plugin
  DECLARED — checked at runtime against the compiled `PackagedFields` (the same facts PLG004
  verified at build);
- the value passes the existing extension spec validation path (type, options, maxLength);
- the write lands in the entity's `ExtensionData` column addressed by **(entity wire key,
  row id)** — no host CLR type in the plugin's hands; tenant isolation stays ambient;
- audited as a field-level change with the PLUGIN as the attributed actor
  (`plugin:invoicing`, operation `effect:{eventType}` or the invoking operation id) —
  provenance is docs/22's "deterministic composition" promise, kept on the write side;
- an entity-modified effect is published so open grids live-refresh like any other write.

Explicitly NOT: writes to compiled host fields or another plugin's keys (state transitions
belong to host operations — EDIT001's philosophy), and no cross-operation transaction seam.
The gate remains the only in-transaction hook; everything else composes through the outbox.

## D-X3 — Declared read dependencies + the host view reader

```csharp
plugin.RequiresView("orders.detail", "id", "number", "status");
```

- **Build() — PLG008**: the named view exists and exposes the named result fields. The
  plugin's compatibility with a host becomes a compile-time fact, an impact-report line, and a
  capability-manifest line ("reads your orders: id, number, status"). This is the wire-key
  redemption of docs/22's abandoned `RequiresHostEntity<Order>()` sketch.
- **Runtime**: `IHostViewReader.RowsAsync(viewId, filters, ct)` — a thin blessing of the
  fortnox pattern (it casts `ViewExecutor` out of `IServiceProvider` today). Two modes matching
  the two contexts a plugin runs in:
  - **actor mode** (gates, operations, integration mappers — a request exists): executes as
    the actor, permission-checked and masked, exactly today's fortnox semantics;
  - **service mode** (effect handlers — no actor): permitted ONLY for views the plugin
    declared with `RequiresView`, tenant-ambient. The `ITamDirectory` shape — narrow,
    tenant-anchored, framework-owned — generalized to declared domain reads. No general
    "plugin superuser": the readable surface is exactly the declared list the install screen
    shows.
- **NOT composable queries**: no cross-boundary IQueryable joins — that would smuggle host CLR
  shapes back through the query provider. Grid-time "order number on the invoice list" stays
  denormalize-at-event-time (the payload carries it); service reads cover validation and
  backfill/repair.
- **Typed wire reads** (`WireValues`): the values a plugin pulls out of gate inputs, effect
  payloads and view rows read through one accessor family — `gate.Guid("orderId")`,
  `effect.String("number")`, `response.FirstRow()?.Decimal("estimatedTotal")` — with one rule:
  missing/null/mismatched reads as `null`, never throws. The `TryGetProperty` ceremony that
  used to pad every handler is gone; the names passed are the same wire names RequiresView /
  RequiresEvent declared.

## D-X4 — Detail slots (BUILT)

The record-bound panel ("invoices on the order detail modal") needs a host-declared SLOT — a
wire contract carrying record context — that plugins bind panels to:

```csharp
model.Slot("web.orders.detail", context => context.Key("orderId", "guid"));      // host
plugin.Panel("web.orders.detail", grid: "invoicing.web.invoices",
    bind => bind.Query("orderId", fromContext: "orderId"));                        // plugin
```

PLG007 validates slot existence + bind shape (a plugin may contribute SEVERAL panels to one
slot — they render in contribution order; the inspect plugin's checklists + line-items pair
on `web.orders.detail` is the shipped example); the manifest gains `slots`; `@tam/react` ships
`<PluginSlot id context>`; the host's custom page drops one line in and every current and
future plugin lands there unnamed. **Deferred** because Step 17 degrades gracefully without it
(the host can `Place()` the plugin's invoices page as a tab beside Orders today) and because
slots are the first brick of "framework-composed pages" — worth designing together with that,
not rushed here. D4: slot ids are permanent host wire names from day one.

## D-X5 — Event contracts (BUILT)

`OnEffect("order-completed")` targets are unchecked and payload shapes are folklore
(defensive reads everywhere). Phase 2: the host (or the generator, derived
from `EventPublished` usages) declares published events + payload fields; PLG009 validates
subscriber/trigger targets at Build(); the manifest gains `publishes` and the impact report a
`SubscribedBy` symmetric with `GatedBy`. `plugin.RequiresEvent("order-completed", "orderId",
"number")` hangs off the D-X3 declaration family.

## D-X6 — The exemplar is a real vendor plugin

`samples/invoicing` joins inspect/fortnox/approvals as the fourth plugin shape: **the one that
extends another domain**. It ships: `Invoice` entity (loose `OrderId` + denormalized
`OrderNumber`), `invoicing.create-from-order` / `invoicing.finalize` / `invoicing.mark-paid`,
the `order-completed` draft subscriber, an `UninvoicedGate` on `orders.complete` driven by the
plugin's own policy, `ExtensionField("order", "invoiceStatus", choice: draft|invoiced|paid)`
written via D-X2, the grid action via D-X1, `RequiresView("orders.detail", …)` via D-X3, nav
contribution suggesting `work`, and the outbound push/poll pair against the mock provider.
Wire verification: a subtree-grid interaction test rides free (create invoice on a child
company's order from the parent's grid — the D-X1 action composes with per-row act-as).

## Step 17 — "Invoicing arrives as a plugin — and Orders still doesn't know"

The chapter's narrative order (each beat names its seam; NEW = built in this milestone):

1. **The vendor's aggregate** — `Invoice` with a wire-key `OrderId`, host opt-in one-liner.
2. **Its own vertical** — operations, list view (Query takes `Guid? OrderId`), form/grid,
   locales, nav suggestion.
3. **Compatibility, stated at build time** — `RequiresView` + actor-mode read validating
   "exists, completed, visible to me" (NEW: D-X3).
4. **The draft writes itself** — the `order-completed` subscriber, idempotent, order number
   harvested from the payload.

Payload values serialize with the platform's wire JSON options: camelCase names and — the
part that matters for cross-module contracts — **enums as their wire strings** (an
`OrderType.Service` payload field arrives as `"service"`, never a number or Pascal name).
A subscriber may match on those strings verbatim; normalizing case is still good hygiene
at a module boundary.
5. **Invoicing pushes back** — the gate on `orders.complete`; manifest shows `gatedBy`.
6. **The order wears its invoice status** — packaged field + the writer (NEW: D-X2); the
   column/filter appear on the host grid with zero host changes.
7. **"Create invoice" where the user lives** — the grid action (NEW: D-X1); the button shows
   only for entitled+activated tenants and permitted users — and works from the parent's
   subtree grid on a child's row.
8. **Money leaves the building** — outbound push + payment poll (fortnox reprise).
9. **The tenant clicks Activate** — entitlement, manifest omission, and the DERIVED
   capability manifest: "reads orders (id, number, status); adds field order.invoiceStatus;
   gates orders.complete; adds an action to your orders list; subscribes to order-completed."
10. **What we didn't need** — the honest close: no host CLR types, no host edits beyond the
    storage opt-in, no plugin front-end code; the detail panel arrives with slots (phase 2).

## Phasing

1. **This milestone**: D-X1 (PLG006 + manifest + client), D-X2 (writer + audit attribution +
   live-refresh), D-X3 (PLG008 + reader, both modes), `samples/invoicing`, Step 17 chapter,
   wire + UI verification, baseline/typed-client regen.
2. **Phase 2**: D-X4 slots (with the framework-composed-pages design), D-X5 event contracts,
   host-action migration to declared binds, host/tenant action suppression.

## Non-goals

Cross-boundary IQueryable composition; plugin-shipped front-end code; writes to compiled host
fields or foreign plugin keys; a cross-operation transaction seam; plugin-to-plugin calls.

## Generated typed facades (the declarations pay twice)

A plugin's `RequiresEvent`/`RequiresView` declarations now also GENERATE typed facades
(Tam.Compiler, same source generator as AddDiscovered): `RequiresEvent("order-completed",
"orderId:guid", "number")` emits an internal `OrderCompletedEvent(Guid? OrderId, string?
Number)` record with `From(EffectEvent)`, and `RequiresView("time.list", "id:guid",
"amount:decimal", …)` emits `TimeListRow` with `From(JsonElement)` — handler bodies get
compile-time names while the WIRE CONTRACT stays the contract:

- The optional `:kind` suffix (`guid|decimal|int|bool`, default string) types the facade
  property; PLG008/PLG009 and the service-mode whitelist see the BARE name — the suffix is
  consumed only by the generator. (Contract-side kinds on PublishesEvent are a deliberate
  deferral: requirements type what they read; the contract still owns names.)
- The dependency direction holds: facades are generated FROM the plugin's own declaration
  in its OWN assembly (internal, `Tam.Generated`) — never a reference to host CLR types.
- The fluent call is read directly by a syntax provider (literal arguments); no attribute
  ceremony. Non-literal arguments simply generate nothing.

## The manifest is the contract artifact (user question, arc in progress)

The facades above generate from the plugin's OWN declaration — which means the shape is
still typed twice: the host writes `PublishesEvent("order-completed", …)`, the plugin
re-types the fields in `RequiresEvent`. The user's challenge lands: *"can't the plugin just
reference the manifest?"* — yes. The manifest already IS the machine-readable contract
(the TS client is generated from it); plugin authoring should consume the same artifact.
The duck typing was never the design goal — it was the residue of not having a consumable
contract artifact on the plugin side.

What does NOT change: plugins still never reference host CLR types (a plugin compiles
against a contract, not an assembly — the cross-vendor requirement), and composition-time
verification (PLG008/PLG009) remains the enforcement — whatever the plugin compiled
against, the REAL host it activates on is checked at Build(). The artifact is compile-time
convenience; the Build() check is the truth.

**Slice 1 — the publisher owns the shape (BUILT).** `PublishesEvent` now takes the same
`"name[:kind]"` grammar as the requirements (`ContractKinds`, one parser for both sides):

```csharp
model.PublishesEvent("order-completed", "orderId:guid", "number");
```

- The manifest's `events` section carries `kinds` (additive) — the contract artifact is
  now complete enough to generate from.
- PLG009 checks kind AGREEMENT: where both sides declare a kind and they differ, the
  build fails ("published as 'guid' but required as 'decimal'"). A typo'd kind is an
  error at declaration, never a silent string.

**Slice 2 — events are records (next).** The honest audit ("is this as beautiful as it
gets?") found the real remaining anomaly: everywhere else in Tam the contract IS a C#
record and the wire derives from it — operations declare Input records, views declare
Result records — but events are declared as STRING LISTS and PUBLISHED as anonymous
objects nothing checks against the declaration. The contract has three edges and only one
is verified: declaration↔consumption is PLG009-checked; declaration↔publish-site is
folklore. So events become the same idiom as everything else:

```csharp
[DomainEvent("order-completed")]
public sealed record OrderCompleted(Guid OrderId, string Number);
…
.Effect(Publish(new OrderCompleted(order.Id.Value, number)))
```

- The contract (fields AND kinds) derives from the record; the string-list
  `PublishesEvent` retires from hand-written models (compile-time discovery registers
  declared event records — the AddDiscovered idiom). Kinds inherit the FULL wire
  vocabulary (money, date, …) from CLR types instead of the five-token facade set.
- The publish site is compile-checked, and an analyzer rule closes the anonymous-object
  hole: publishing a payload that is not a declared event record is a build error.
- The manifest events section becomes fully DERIVED — an artifact nobody hand-maintains.

**Slice 3 — generate the plugin's contracts FROM the host artifact.** A plugin
project references the host's exported manifest (an `AdditionalFiles` item, versioned in
the plugin repo like a lockfile); the source generator emits the typed facades from the
artifact's `events`/view sections instead of from the plugin's re-declaration, and the
`RequiresEvent`/`RequiresView` calls shrink toward id-only — the generator emits the
field/kind requirements INTO the model registration from the snapshot, so PLG009 still
verifies the full shape against the real host at composition time. Updating the host
dependency = replacing one json file; every rename or kind change becomes a compile error
in the plugin (D4 makes this stable: wire names are permanent, so snapshots age
gracefully). RequiresView kind checking against the view's REAL wire kinds (money↔decimal
compatibility) rides this slice.

**The end-state scorecard (what "as beautiful as it gets" means here).** ONE hand-written
declaration — the host's event record / the view's Result record. Everything else is
derived or verified: the publish site (compiler), the manifest contract (derived), the
plugin's facade (generated from the artifact), the requirement registration (generated),
and the composition check against the real host (PLG008/PLG009). The two mirrored record
types — host's and plugin's — are inherent to the no-shared-assembly constraint (the
same way the TS client mirrors server types) and neither is maintained by hand. What
deliberately STAYS stringly: bind sites (grid actions, panels, context keys) — checked at
Build(), and typing them would buy ceremony, not safety.
