# 32 — Framework-composed pages: the list-and-detail shape leaves app code

Status: **v1.1 BUILT** (ordered SECTIONS at both levels; erp's OrdersPage React component
deleted). Decisions D-P1..D-P5.

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
calls — and the record surface is likewise an ordered list of `Form(...)`/`Slot(...)` sections.
A slot declared before the form renders above it; a second grid renders after the first.

- **D-P1 — a page is a host-declared composition of things the model already has**: one grid,
  and optionally a RECORD surface — detail view + context key, optional edit form, optional
  title field, any number of slots. `PAGE001` verifies every part exists and fits (the key is
  a real query field, the title a real result field, the slots declared). Pages are the
  host's, like nav layout and slots (PLG005): plugins reach pages through slots and grid
  action contributions, never by declaring pages.
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

Form prefill is the row-action convention made declarative: each form field takes the
same-named detail result field; the record's `key` field takes the clicked row's id. The
manifest gains a `pages` section (host-only, no activation filtering — the slots inside filter
themselves).

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
