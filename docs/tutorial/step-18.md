# Step 18 — The composed UI: nav, pages, slots, and the subtree grid *(BUILT — [30-navigation.md](../30-navigation.md), [32-pages.md](../32-pages.md), docs/26 D-H1)*

Seventeen steps in, the model knows everything — operations, views, forms, grids, fields,
gates, actions, panels — and yet one surface was still hand-wired: the shell. Norrservice's app
carried a flat hand-written nav list and a ~50-line `OrdersPage` React component whose every
line was derivable (render the grid, on row click fetch the detail, open a modal with the edit
form, host the plugin slot). Both are gone. This step is where the whole UI becomes
manifest-composed — and, as always, the point is what we do NOT write.

**Nav is a declared tree.** The host owns layout; here is Norrservice's, in the model builder:

```csharp
.Nav("web", nav => nav
    .Mode("work", m => m
        .Page("orders", page: "orders", order: 10)       // DECLARED pages (below)
        .Page("customers", page: "customers", order: 30))
    .Mode("admin", m => m
        .Section("administration")))
```

Two rules make this scale past one app. **Contribution is not placement**: the invoicing plugin
declared `plugin.Nav(nav => nav.Page("invoicing.invoices", grid: "invoicing.web.invoices",
suggest: "work", order: 40))` — a *suggestion* of a semantic section, never a position in a
layout it cannot see; the framework's own admin packages suggest `administration` the same way,
and the host's `Section("administration")` collects them. Anything nobody places lands on an
auto-page under a well-known "more" section in the last mode — nothing can be authored into
invisibility; declaring nav is how a plugin graduates from "appears" to "belongs". And
**visibility is derived, never authored**: a page shows iff the actor holds the bound view's
permission, a section or mode shows iff any descendant does — so a role-oriented workspace
falls out free (a technician whose permissions empty the `admin` mode never sees the switcher
entry), and there is no `visibleWhen` to audit. Nav is discoverability, never authorization:
hiding removes the menu entry, not the surface. The manifest gains a `nav` section per surface
class (`"nav": { "web": [ { "id": "work", "kind": "mode", "labelKey": "nav.work",
"children": [ … ] } ] }`), labels are locale keys like everything else, node ids are D4-permanent,
and a tenant overlay closes the loop: the `tam.nav` package ships `nav.override` (hide,
per-culture relabel, reorder, move a page into another section — a CLOSED mutation set) and
`nav.retire` (restore the default) as ordinary audited operations over registry rows, overlaid
onto the tree at the manifest route. It is the custom-fields pattern applied to navigation —
tenant customization as data, never code — and an override whose node vanished (say the plugin
was deactivated) is dormant, not broken: reactivate and the tenant's placement returns intact.

**The orders page is a declared composition.** The React component this replaces is deleted:

```csharp
.Slot("web.orders.detail", slot => slot.Key("orderId"))     // the contribution point (Step 17)

.Page("orders", page => page
    .Grid("web.orders.list")                        // sections render in DECLARATION ORDER
    .Record(record => record
        .Detail("orders.detail", key: "orderId")    // fetched with the clicked row's id
        .Title("number")                            // detail field shown in the record title
        .Form("web.orders.edit")                    // prefilled from same-named detail fields
        .Slot("web.orders.detail")))                // invoicing's panel lands here, unnamed
```

A page is an ordered list of sections; the first grid opens the record surface, itself an
ordered list of form and slot sections — declaration order IS layout order, so there is no
`after:` annotation to disambiguate. Form prefill is the row-action convention made
declarative: each form field takes the same-named detail field, the record's key takes the
clicked row's id. `PAGE001` verifies every part exists and fits at build time; `SLOT001` fails
the build on a declared slot no page renders (a slot nobody renders is plugins contributing
panels into a void — the nav "more" lesson, applied to slots; a slot the app hosts in custom
React declares `external: true` and nothing else). Pages are the host's, like layout: plugins
reach them through slots and grid-action contributions, never by declaring pages. And the nav
entry above needed no permission atom — a `{ page }` target naming a declared page derives its
visibility from the page's grid's view, exactly like a `{ grid }` target. `registerPage(key,
component)` remains the escape hatch for genuinely custom UX, and its ratio is the architecture
tripwire: if most pages need React, the model is decorating the app. Norrservice is at zero.
Customers got the same treatment — a second `.Page("customers", …)` declaration in Program.cs,
grid + record + edit form, no slot — proving the shape generalizes without ceremony.

**The subtree grid.** Step 15 made Norrservice a group; here is what the group *sees*. The
orders list — the same view, not a twin — declares one capability:

```csharp
public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
    .Sortable(nameof(Result.Number), nameof(Result.CustomerName), nameof(Result.RequestedDate))
    .Filterable(nameof(Result.Status), nameof(Result.Type))
    .SubtreeRead(nameof(Result.TenantId))           // docs/26 D-H1: THE list is also the roll-up
    .DefaultSort(nameof(Result.Number), descending: true);
```

Standing at a leaf, nothing changes. Standing at `demo`, the ambient READ scope widens to the
acting node's validated subtree for that execution — the authored query is untouched — and the
named result field becomes three mechanical client behaviors at once: a **company column**
(labeled by tenant display name, rendered only when there is more than one company to tell
apart), a **tenant filter**, and **per-row act-as** — clicking a `nord` row fetches the detail,
submits the edit form, and fires row actions with `X-Tam-Tenant: demo.nord`, so every write
still fans in to exactly one node (writes never widen; the stamp reads only the current node).
Step 17's contributed "Create invoice" button composes for free: on a child company's row it
acts in that company. The manifest carries one fact — `"subtree": "tenantId"` on the view —
and the one sharp edge stays compile-checked: a query composing a widened source must scope its
other sides explicitly (`InScope` beside `WithInherited`), or TAM005 fails the build.

**The frontend, in its entirety.** The app shell composes slot components and owns nothing else:

```tsx
<NavProvider>
  <NavModeSwitcher />   {/* depth 0 — modes */}
  <NavSidebar />        {/* depth 1 */}
  <NavTabs />           {/* depth 2 */}
  <NavPage />           {/* declared pages via <ModelPage>, grids via <ViewGrid>, registered pages by key */}
</NavProvider>
```

**What we did NOT write:** routes, menu arrays, page components, modal wiring, a
detail-fetch-then-prefill effect, a company column, a tenant filter, an act-as plumbing layer,
or any per-plugin nav code. The sample's last hand-written page is deleted, and every plugin
from Steps 13–17 landed in this shell without the shell knowing their names.

---
