# 32 — Framework-composed pages: the list-and-detail shape leaves app code

Status: **v1.2 BUILT** (ordered SECTIONS at both levels; plugin-declared pages; erp's
OrdersPage React component deleted). Decisions D-P1..D-P6. The sample declares THREE pages —
orders (host, with slots), customers (host, plain), invoicing.invoices (PLUGIN, read-only
record) — and the registerPage count is still zero.

## The problem

Nav v1 (docs/30) left three page target kinds: `{ grid }` rendered generically, `{ plugin }`
the mechanical fallback, and `{ page }` — an app-registered React component. The sample's one
custom page (OrdersPage) was ~50 lines of React whose every line was derivable: render the
grid, on row click fetch the detail view, open a modal with the edit form prefilled, host the
plugin slot. That is not custom UX; it is the STANDARD record shape hand-written — and every
host would write it again for every aggregate.

## The model

```csharp
model.Page("orders", page => page
    .Grid("web.orders.list")                       // sections render in DECLARATION ORDER
    .Record(record => record
        .Detail("orders.detail", key: "orderId")   // fetched with the clicked row's id
        .Title("number")                           // detail field shown in the record title
        .Form("web.orders.edit")                   // prefilled from same-named detail fields
        .Slot("web.orders.detail")));              // plugin panels (docs/31 D-X4)
```

A page is an ORDERED list of sections — any number of `Grid(...)` and page-level `Slot(...)`
calls — and the record surface is likewise an ordered list of `Form(...)`/`Grid(...)`/`Slot(...)`
sections. A multi-section page labels its sections with `Grid(id, heading: "headings.key")`
(docs/34 M6) — a locale KEY, L10N001-gated like every label; single-section pages need none.
A slot declared before the form renders above it; a second grid renders after the first.

### Record tabs (arc 4)

A record's sections group into TABS instead of stacking — the record becomes a multi-page
surface, extendable with new tabs (including plugin ones) without touching the others:

```csharp
.Record(record => record
    .Detail("work-orders.detail", key: "workOrderId")
    .Title("number")
    .Tab("details", "erp.tabs.details", s => s.Form("web.work-orders.edit"))
    .Tab("time", "erp.tabs.time", s => s
        .Grid("web.time.list", bind => bind.Query("workOrderNumber", fromRecord: "number")))
    .Tab("materials", "erp.tabs.materials", s => s
        .Grid("web.materials.list", bind => bind.Query("workOrderNumber", fromRecord: "number"))))
```

- A **`Grid` record section** is a child listing filtered off the open record: each
  `bind.Query(param, fromRecord: field)` fills a grid query param from a detail-view field, so
  the child filters MECHANICALLY (docs/20) — a work order's time entries, no dedicated view.
  PAGE001 verifies the grid exists and every bound field is a detail-view result field.
- **Tabs are the plugin extension point too**: a `Slot(...)` section in a tab surfaces plugin
  detail panels (docs/31 D-X4) as their own tab — the host opts the slot into a tab once and
  every current/future plugin lands there (the order detail's "checklists & invoices" tab).
- A record declares tabs OR flat sections, not both (PAGE001).
- The open record is **URL-routed** (`?record=<id>`, riding the nav's `?mode=&page=` from the
  FE structural pass): a record view is deep-linkable and the browser Back button closes it.

- **D-P1 (evolved, review round 4) — a page is a DECLARED composition of things the model
  already has**: one grid, and optionally a RECORD surface — detail view + context key,
  optional edit form, optional title field, any number of slots. `PAGE001` verifies every
  part exists and fits (the key is a real query field, the title a real result field, the
  record form's operation carries the key input, the slots declared). Pages were originally
  host-only; a plugin may now declare pages for ITS OWN aggregates (the id sits under the
  plugin prefix, PLG001; the manifest filters them by activation) — EXISTENCE is the
  declarer's, PLACEMENT stays the host's and tenant's through the nav machinery. A record
  with no form renders the detail view's fields read-only (the invoicing page: status moves
  through operations, never an edit form). Layout (slots on host surfaces, nav trees) remains
  host-declared.
- **D-P2 — `registerPage()` is the escape hatch, not the default.** Nav `{ page }` targets
  resolve a registered React page first, then a declared page (rendered by `<ModelPage>` in
  tam-react: grid → row click → detail fetch (with per-row act-as for subtree rows) → modal
  with form + slot panels). The registerPage RATIO is the architecture tripwire (the docs/19
  direction review): if most pages need React, the model is decorating the app. The sample is
  now at zero.
- **D-P4 — declaration order IS layout order; the FIRST grid opens the record.** Position
  hints are the structure itself, not `after:` string annotations — an ordered composition has
  nothing to disambiguate. Page-level slots carry no record context (their panels render
  unbound — plugin widgets); record slots receive the record context.
- **D-P5 — SLOT001: an orphaned slot is a build error.** A declared slot referenced by no page
  is authored into invisibility (plugins would contribute panels nothing renders) — the nav
  "more" lesson applied to slots. A slot the app places in custom React declares
  `external: true`, which exempts it from the check and nothing else.
- **D-P3 — declared pages derive nav visibility from their grid** (NAV005 relaxed): a nav
  `{ page }` target may omit its explicit permission when the key names a declared page —
  visibility comes off the page's grid's view, exactly like `{ grid }` targets. Registered
  React pages still require the explicit atom (there is no manifest surface to derive from).

**Convention over enumeration (D-P6).** `Form<T>(id, op)` without configure binds EVERY
operation input field in record declaration order — the record IS the form; `Grid<T>(id, view)`
without configure makes every result field a column (minus `id`/`version` row plumbing and
object-shaped fields). Configure exists to DEVIATE — subset, reorder, renderers, visibility,
actions — never to enumerate what the record already states. An empty column/field list in a
provided configure gets the same defaults, so a grid that only declares actions stays one line.

**Row actions execute; they do not open forms** (M6 corrected this paragraph against the
code): a grid `RowAction("op")` runs the operation IMMEDIATELY on click, filling each
REQUIRED input from the same-named row column and falling back to the row's `id`
(`orderId → row.orderId ?? row.id`). That fits id-shaped intents — complete, retire,
check — which is what row actions are for. An operation that needs typed input belongs on
a record surface (its form prefills from same-named detail fields) or as a toolbar action
(opens the operation's form blank). Plugin-contributed grid actions are the third shape:
their `bind` map states input ← column explicitly (docs/31). A declared action-input
mapping in the manifest remains the designed replacement for the same-name convention.

**`RowForm("op")` is the fourth shape — the EDIT affordance**: it opens the operation's
form PREFILLED from the row (same-named result fields → form fields) instead of executing.
Pair it with an upsert operation whose list view deliberately carries the full definition
in its result record — `rules.list` ↔ `rules.define` is the canonical pair: the grid shows
three columns, the row DATA carries condition/messages/action, and the row form edits the
rule in place. It is the record-surface prefill convention applied to grids — no new
mapping concept.

Form prefill is the row-action convention made declarative: each form field takes the
same-named detail result field; the record's `key` field takes the clicked row's id. The
manifest gains a `pages` section (plugin pages activation-filtered like every contribution;
the slots inside filter themselves).

## readOnly packaged fields (the docs/31 wrinkle, closed)

`plugin.ExtensionField(..., readOnly: true)` marks plugin-owned state: it renders in grids and
filters like any extension field but is EXCLUDED from forms, and the wire extension channel
rejects writes (`extensions.read-only-field`) — only the owning plugin's `IPackagedFieldWriter`
sets it. Tenant-defined fields are never read-only. Invoicing's `invoiceStatus` uses it: the
status shows everywhere, and its state machine stays the plugin's.

## Non-goals (v1)

Dashboards and free-form layouts (registerPage territory until real demand shapes a model);
multi-record master-detail; tenant-customized page layouts (would ride the nav-override
registry pattern if ever); page-level plugin contributions beyond slots.
