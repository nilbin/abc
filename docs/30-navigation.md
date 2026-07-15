# 30 — Navigation: a declared tree, slotted levels, modes, and a tenant overlay

Status: **v1 BUILT** (declared tree, merge + NAV000-005, manifest `nav`, tam-react slot
components, package declarations, App.tsx on slots). v2 (tenant overlay) is next. Decisions
D-N1…D-N8. Owner calls made: modes are the top level of the tree; hiding is presentation-only;
per-surface trees with `web` fallback. v1 additions settled in code: `Place()` adopts content
but the HOST's order replaces the contribution's; a plugin that declared any nav contribution
never also gets the mechanical fallback page.

## The problem this solves

Navigation is the one client surface the manifest does not drive. The sample app hand-wires a
flat `NavLink` list — three domain pages, then the framework-admin pages, each guarded by an
inline `can(...)` — even though every admin surface is a framework package shipping its own
grids. Plugins fare better (their nav IS manifest-derived: grids grouped by active plugin id,
permission-filtered) but only mechanically: one flat entry per plugin, no placement, no
hierarchy. Three requirements break this shape together: **multiple levels** rendered in
different UI slots (left sidebar → in-page top bar → dropdowns), **modes** (named top-level
setups with a quick switcher), and **tenant customization** riding the house registry pattern.

## The model

### A nav node

```
NavNode {
  id          — permanent identity (D4 rules; tenant overrides address nodes by it)
  kind        — closed set: mode | section | page
  labelKey    — locale key, by convention "nav.{id}" (D6 — never text)
  icon?       — semantic icon HINT from a small vocabulary; the renderer maps hints to glyphs
  order       — sort within the parent; contributions without one append
  target?     — pages only, a closed union:
                  { grid: "web.orders.list" }   — rendered generically (ViewGrid stack)
                  { page: "orders" }            — an app-registered custom page key
                  { object: "coolingUnit" }     — custom-object standard page (docs/23, later)
  permission? — { page } targets only; an EXISTING catalogue atom, validated at build
  plugin      — contributing plugin/package id, stamped like every other contribution
  children[]  — hierarchy IS the nesting; no slot names in the model
}
```

Visibility is **derived, never authored**: a `{ grid }` page is visible iff the actor holds the
bound view's permission (read off the manifest exactly as the client computes it today);
sections/modes are visible iff any descendant is. No `visibleWhen`, no per-node grants.

### Contribution is not placement

Plugins/packages declare content plus a SUGGESTED semantic section slug (`work`,
`administration`, `settings`, host-defined). The HOST's declaration decides actual layout
(`model.Nav("web", …)` with modes/sections and explicit `Place(nodeId)`); the tenant overlay
gets the final word. A plugin never names a mode, slot, or position in a layout it cannot see —
the same reasoning that keeps plugins on wire keys instead of host CLR types (docs/22).

**Mechanical fallback:** any grid no declaration places lands on an auto-page per contributing
plugin under a well-known `nav.more` section in the last mode — nothing can be authored into
invisibility; declaring nav is how a plugin graduates from "appears" to "belongs". Framework
packages ship declarations (suggesting `administration`) from day one.

### Levels and slots

The model carries pure hierarchy (depth + kind); the renderer maps depth → slot. Reference
mapping in `@tam/react`: depth 0 (mode) → mode switcher; depth 1 → left sidebar; depth 2 → top
bar/tabs within the parent page; depth 3 → dropdown. Depth cap: mode + 3, enforced at build
(host/plugin) and definition (tenant regroup). A single-mode app renders no switcher; a mobile
renderer may collapse differently — the model neither knows nor cares.

`@tam/react` ships: `useNav()` (effective tree off the manifest, permission-filtered, active
trail + mode state), `<NavModeSwitcher/>`, `<NavSidebar/>`, `<NavTabs/>` slot components,
`registerPage(key, component)` (the `{ page }` registry — the registerRenderer pattern), and a
generic grid-target page (today's PluginPage, generalized into the library). The app composes
the slots into its shell and owns routing/URLs.

### Modes

A mode is a depth-0 node — not a separate filter axis: one tree, one overlay, one merge, one
answer to "where is this page". Role-oriented workspaces fall out free: a mode whose every
descendant requires permissions a user lacks vanishes. The client remembers the last active
mode per user; default is the first visible. **Modes are strictly orthogonal to act-as
(docs/26)**: act-as changes the data/permission universe server-side; a mode changes
presentation over one manifest, zero requests. They compose; neither implies the other.

### Tenant customization (v2): the nav override registry

The extension-field pattern verbatim — registry data, audited operations, retire-don't-delete,
overlaid per tenant into the effective manifest. A `tam.nav` framework package ships:
`nav.override` (closed mutation set: `hidden`, per-culture `labels`, `order`, `parent`),
`nav.define-section` (tenant-authored section/mode, id prefixed `tenant.{key}`, key reserved
forever), `nav.retire` (restores the declared default), and the `web.nav` admin grid.
Definition-time diagnostics NAV001–005 (unknown/retired node; cycle/depth; illegal parent kind;
missing culture label; empty-for-every-role warning).

Merge: host tree ⊕ package/plugin contributions ⊕ tenant overrides → manifest nav per tenant;
the client filters per actor at render. An override whose node vanished (plugin deactivated) is
dormant, not broken — reactivate and the tenant's placement returns intact.

**What a tenant can NOT do — nav is discoverability, never authorization:** cannot make
anything visible past permissions; hiding removes the menu entry, not the surface (a direct URL
to a permitted grid still works — a presentation control must never be mistakable for an
enforcement control, docs/27); cannot rebind targets, author expressions, or add external links.

### Wire shape

`ManifestDto` gains a `nav` section keyed by **surface class**, the axis binding ids already
encode (`web.*`/`mobile.*`): `"nav": { "web": [ …NavNode tree… ], "mobile": [ … ] }`. Mobile
falls back to `web` until declared. **MCP and headless consumers get no nav** — agents navigate
the operations/views catalog; nav is an additive section they ignore. D4 applies to node IDS
(baseline-checked; retire, never remove — overrides address them), while structure (moves,
reorders, relabels, additions) is free to evolve.

## Decisions

- **D-N1 — nav is a declared tree in the model, with mechanical fallback for the undeclared.**
  Derivation-from-grids becomes the safety net, not the design: it cannot express hierarchy,
  ordering, or cross-plugin grouping.
- **D-N2 — contribution is not placement.** Plugin suggests; host places; tenant overrides.
- **D-N3 — the model carries pure depth + kind; the renderer maps depth to slot.** Slot names in
  the wire model would freeze one web layout into the contract. Depth cap mode + 3.
- **D-N4 — modes are the top level of the tree, presentation-only,** orthogonal to act-as.
- **D-N5 — tenant customization is a nav override registry** (`tam.nav`, NAV### diagnostics),
  merged as an overlay; tenant packages (P3) can carry nav overrides in bundles.
- **D-N6 — nav is discoverability, never authorization.** Visibility derives from the bound
  surface's permission; hiding is presentation-only; filtering stays client-side like grids.
- **D-N7 — nav node ids are permanent; structure is free.**
- **D-N8 — nav is per surface class** (`web`, `mobile`, …), `web` the fallback, none for MCP.

## Phasing

1. **v1 — the declared tree** (no tenant data): `NavNode`/`NavBuilder` in Tam.Core,
   `plugin.Nav(...)` + `model.Nav(surface, …)`, suggestion collection + fallback, manifest `nav`
   section + baseline coverage, NAV build diagnostics, `@tam/react` `useNav` + slot components +
   `registerPage`, mode switcher, package declarations, App.tsx rewritten onto the slots.
2. **v2 — the tenant overlay**: `tam.nav` package, override entity + operations, NAV001–005,
   per-tenant merge, admin grid, bundle support.
3. **Later**: per-user pins/mode defaults (the docs/12 user-preference overlay tier),
   `{ object }` targets when docs/23 lands, badges/counters on real demand.

## Non-goals

No tenant-defined pages/routes/URLs; no per-node permission grants or `visibleWhen`; no
slot/layout vocabulary on the wire; no external links (closed target union keeps overrides
validatable); no breadcrumb/router framework; no usage-derived automatic IA.
