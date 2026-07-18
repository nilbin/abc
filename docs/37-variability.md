# 37 — Variability: country, trade, size

**Status: designed, not built.** This doc answers the question the field-service domain forced:
*how can order surfaces, forms — and maybe flows — differ by the company's country, by trade
(electrician vs plumber), and by company size, without forking the product?* The answer is a
composition of three mechanisms the framework already has, plus one new registry that mirrors
nav v2 exactly. Nothing here invents a second model.

Revision 2 (user-directed): **a country is a plugin**, not a bundle of presentation presets.
ROT/RUT settled it — Swedish tax deductions are operations, packaged fields and a tax-authority
integration, none of which presentation machinery can carry. This revision reassigns country to
the activation channel, adds the axis/bridge composition model, and promotes the
plugin-on-plugin tier (docs/22 "the tier that isn't (yet)") from someday to prerequisite.

## The spine: one compiled model, three variation channels

There is exactly ONE compiled model per deployment. Variation is never conditional compilation,
never `if (country == "NO")` in domain code, never a per-tenant build. Every axis of variation
routes through one of three channels, each already proven elsewhere in the framework:

1. **Activation + entitlements** — *what capability exists here* (docs/22, docs/24). Plugins
   activate per tenant; plans entitle plugins and seats. This channel answers trade, size AND
   country — anything that adds fields, operations, integrations or structure.
2. **Overlays** — *how a declared surface presents* (docs/30 nav v2 is the template). Registry
   data merged into the effective manifest per tenant, fingerprinted into the ETag, dormant when
   its target vanishes, retire-restores-the-default. This channel answers presentation
   differences — and is the material packs on channel 1 ship their form variants as.
3. **Tenant data** — *facts and behavior the tenant owns* (extension fields docs/15, automation
   rules P5, nav overrides). Rules already gate and mutate host operations per tenant; "flows
   differ by country" is rules + approval gates shipped as presets, not a new engine.

The design work is assigning each user-visible difference to the right channel — and refusing
mechanisms (a form DSL fork, per-tenant field visibility in C#) that blur them.

## Tenant profile facts

A small, closed vocabulary of facts on the tenant node: `country` (ISO 3166-1 alpha-2),
`trades` (a set of slugs from a host-declared catalog: `electrical`, `plumbing`, …), `size`
(a band: `micro`/`small`/`medium`/`large` — bands, not headcounts, so the fact is stable).
Written by `tenants.set-profile` (audited, validated against the catalogs), read by the
overlay/activation machinery, exposed in the manifest overlay so the client can read them too.

Facts are **keys into registries, never branch points in code**. A pack overlay says "when
`country=NO`, present the address form this way"; domain code never reads a fact. And facts
are **presentation/default selectors, never authorization** — the D-N6 lesson applies
verbatim: a profile fact must not be mistakable for an enforcement control. (Size may gate
capability, but it does so through the plan — the anchor's entitlements, docs/24 — not through
the fact; the fact at most picks defaults, e.g. which plan a trial starts on.)

Facts inherit down the tenant tree the way subscriptions do: nearest ancestor-or-self value
governs, so a Norwegian subsidiary under a Swedish group sets `country=NO` once at its node.

## The form overlay registry (`tam.forms`)

Nav v2's pattern, applied to forms — the extension-field pattern verbatim: registry data,
audited operations, retire-restores-the-declared-default, overlaid per tenant into the
effective manifest, fingerprint joins the manifest ETag. A `tam.forms` framework package ships
`forms.override`, `forms.retire`, a `forms.overrides` view and a `web.forms` admin grid riding
the D-P6 defaults.

`forms.override` targets a declared form id (`web.orders.create`) and carries a **closed
mutation set** per field:

- `hidden` — remove from presentation. Legal only for fields the server does not require
  (or that carry a default); the operation validates this at definition time.
- `order` — reorder fields.
- `labels` — per-culture relabel, merged into the catalogs like nav labels.
- `required` — **tighten only.** An overlay can make an optional field required (client-side
  affordance + resolve warning); it can never un-require what the server requires. Server
  validation is the truth; the overlay only narrows what reaches it.
- `group` — assign fields to named, ordered groups (the presentation affordance multi-field
  value objects like the address need).
- `validators` — select a named validator set for the field (below).

**What an overlay can NOT do** — the mirror of nav's "discoverability, never authorization":
it cannot add fields (that is extension fields' job — a different channel with storage behind
it), cannot touch the wire contract (names, kinds, operation shape are D4-permanent), cannot
loosen validation, cannot make ungranted data visible, cannot rebind lookups or author
expressions. A hidden field still round-trips its default; the operation still validates.

Definition-time diagnostics FRM001–005 (unknown form/field; hiding a required-without-default
field; unknown validator set; unknown group reference; missing-culture label warning) — the
NAV001–005 idiom. An override whose field vanished (plugin deactivated, form evolved) is
**dormant, not broken**, exactly like nav's dormant moves.

### Two authorship tiers, fact-keyed selection

- **Tenant-authored**: the admin edits their own forms in `web.forms` — the nav.override story.
- **Pack-shipped**: a plugin or tenant package carries overlay **presets** keyed by a `when`
  clause over profile facts (`when: { country: "NO" }`, `when: { size: "micro" }`). One
  shipped preset serves every matching tenant. This is how a country pack delivers its form
  variants — the presets are part of the plugin, not an alternative to it.

Merge precedence, most specific wins per mutation: declared form ⊕ pack presets (ordered by
`when` specificity) ⊕ tenant overrides. The tenant always wins over packs — their product.

## Named validator sets

The one place variation touches the server. A validator set is **model-declared** (C#, compiled,
named — `address.se`, `address.no`: postal-code format, region requiredness), selected per
tenant by overlay or pack preset. Selection can only ADD validators on top of the field's base
validation — the superset's own rules always run, so a tenant with no set selected is still
safe, and a wrong selection can reject, never admit, bad data. Sets ride the manifest so the
client pre-validates with the same rules (the portable-AST discipline of docs/05 — one
definition, both sides).

## The proving consumer: the structured address

Today an order's location is one text field (`workAddress`). The real thing is a value object
whose *shape* is the **superset of every target country** — street, number, postal code, city,
region, country — declared ONCE in the model (D4-additive: the text field retires, the
structured fields arrive). Country variation is then pure channel-2/3 material:

- presentation: a pack preset per country hides `region` where it's meaningless, reorders
  postal-code-before-city vs after, relabels per culture — `forms.override` mutations, nothing
  more;
- validation: `address.{country}` validator sets (postal-code regex, region catalogs);
- defaults: the tenant's `country` fact prefills the country field.

The address is the acceptance test for this whole design: if it needs a mechanism beyond
facts + overlays + validator sets, the design is wrong. It is also the first piece of the
parked order-domain arc (participants, multi-day, location), so building it pays twice.

## The axis packs: country, trade — and size stays a plan

- **A country is a plugin** (usually a small family). The evidence is concrete: Sweden needs
  ROT/RUT — a deduction type on the order and invoice, labor-cost shares, buyer identity,
  per-person yearly caps, and a Skatteverket submission flow (reimbursement claims). That is
  packaged fields, operations, rules and an outbound integration — channel 1 material through
  and through. Germany needs sectioned order lines (Titel/positions in the construction
  idiom), GAEB-flavored exchange and XRechnung/ZUGFeRD e-invoicing. Per-country accounting
  integrations follow the same shape — the Fortnox sample already IS a Swedish-market plugin.
  A country pack therefore ships the full range: packaged fields + operations + integrations
  (only a plugin can), AND locale catalogs, form overlay presets and validator sets (the
  presentation machinery below, used as its materials).
- **A trade is a plugin** (or a package of them). The electrician pack ships inspection
  checklist templates, a materials catalog, rule presets (e.g. certification checks gating
  `orders.complete`), nav suggestions, form presets. Install + activate = the product speaks
  electrician. Nothing in the host names a trade; `trades` facts at most drive install-time
  suggestions ("tenants like you activate …").
- **Size is a plan** (docs/24). Capability differences by size are entitlements at the anchor —
  never overlay tricks. Presentation simplification for small shops ("hide the project fields,
  one-person companies don't dispatch") is a pack preset `when: { size: "micro" }` — visual
  decluttering over the same capability set, honest to D-N6. Size does not need to be a pack:
  it has no domain machinery of its own, only more-or-less of everyone else's.

The superset principle (D-V8) still governs what lands in the HOST: structure that several
countries need in different flavors belongs in the host as an optional superset the pack
lights up. The German line-section case is the test: if order lines gain an optional
grouping/section concept in the host model, the DE pack contributes presentation and exports
over it — it does not fork the order. Whether sections clear the bar for the host or stay
DE-pack-private structure is a call to make when that pack is real; the default lean is
host-superset, because two countries wanting different flavors of the same structure is
exactly what forks look like at birth.

## Combining axes: the matrix is sparse — bridges, not mega-packs

Country × trade × size must not produce N×M×K packs. The composition model:

- **Axis packs stay pure.** The `se` pack knows nothing about trades; the `electrical` pack
  knows nothing about countries; axis packs NEVER depend on each other. Each is written
  against the host contract alone, exactly as plugins are today.
- **Real intersections become bridge packs.** ROT vs RUT eligibility is genuinely
  country×trade (ROT covers construction trades, RUT household services); a German
  construction-trade pack may need GAEB specifics a plain DE pack shouldn't carry. Such a
  feature is a SMALL plugin depending on both axis packs' contracts — `se × electrical`
  ships only the intersection. The matrix is sparse in reality: most cells need nothing,
  so the handful of bridges that exist are the ones reality demanded, never a generated
  grid of variants.
- **Data before code.** If the intersection is expressible as configuration of an axis pack
  (which deduction types the tenant enables, a rule preset, an overlay), no bridge pack —
  the tenant setting or the fact-keyed preset carries it. A bridge pack exists only when the
  intersection needs BEHAVIOR neither parent can host as data.
- **Auto-activation kills matrix administration.** A bridge pack declares
  activate-when-parents-active: when both parent packs are active on a tenant (and the plan
  entitles it), the bridge activates mechanically; deactivate a parent and the bridge
  suspends (the dormancy idiom again — reactivate and it returns). Tenants pick axes; the
  matrix manages itself.
- **Layering stays shallow by convention**: host → axis packs → bridge packs. Depth two,
  acyclic by construction (bridges depend only on axis packs, axis packs only on the host).
  Nothing deeper is designed until something real demands it.

## Plugin-on-plugin: promoted from someday to prerequisite

Bridge packs need the tier docs/22 deliberately left unbuilt ("the tier that isn't (yet)").
The contract side is already generalized: every plugin's contribution is a manifest slice
with declared events, views and kinds, so a plugin can EXPORT its contract artifact exactly
as the host exports `host-contract.json`, and a dependent compiles typed facades from it
identically (docs/31 slice 3 machinery, second provider). What must be designed and built —
the existence semantics docs/22 already enumerates:

- the declared dependency edge (`DependsOn("se")`, derivable from requirement targets'
  owners) with activation ordering, cascade/suspend on parent deactivation, and the
  auto-activation flag above;
- entitlement coupling across the edge (docs/24): no selling the bridge without its parents,
  uninstall coherence;
- PLG010 relaxes from "no inter-plugin targets" to "inter-plugin targets only along a
  DECLARED, acyclic dependency edge, resolved through the parent's exported contract" —
  ownership verification stays, the graph just gains sanctioned edges;
- namespace discipline unchanged: a bridge extends its parents' surfaces (gates on their
  operations, subscribers on their events, packaged fields on their extensible entities,
  panels on their slots) but mints ids only in its OWN namespace.

## Flows

"Flows differ by country" decomposes into pieces that all exist: approval rules (docs/24's
wildcard gates + samples/approvals) are tenant data — a pack ships them as presets; automation
rules (P5) mutate and chain operations per tenant; entitlements decide which operations exist
at all. No workflow engine is introduced. If a real case ever demands sequencing the rules
cannot express, that is a new design conversation — not a quiet extension of this one.

## Decisions

- **D-V1 — one compiled model; variation = activation + overlays + data.** Never per-tenant
  builds, never profile branches in domain code.
- **D-V2 — tenant profile facts are a closed vocabulary** (`country`/`trades`/`size`) written
  by `tenants.set-profile`, inherited nearest-ancestor-or-self, used as registry keys only.
- **D-V3 — form variation is an overlay registry** (`tam.forms`), the nav v2 pattern verbatim:
  closed mutation set, FRM### diagnostics, dormancy, retire-restores-default, ETag fingerprint.
- **D-V4 — overlays present; they never authorize, never add, never loosen.** Tighten-only
  requiredness; extension fields remain the only way to add; the server contract is untouched.
- **D-V5 — pack presets are fact-keyed; the tenant's own override wins.**
- **D-V6 — validator sets are compiled and additive**: model-declared, manifest-carried,
  selection can only tighten.
- **D-V7 — country = plugin, trade = plugin, size = plan.** A country pack carries domain
  machinery (ROT/RUT, line sections, integrations) AND ships presentation materials (locale,
  overlay presets, validator sets). Size alone has no machinery — it stays entitlements.
- **D-V8 — supersets over forks**: an entity that varies by country is declared once as the
  superset of shapes; variation is presentation + validation selection. The structured address
  is the reference case; order-line sections are the pending structural test.
- **D-V9 — axis packs are pure; intersections are bridge packs.** Axis packs never depend on
  each other; a real country×trade feature is a small plugin depending on both parents'
  contracts. Data before code: configuration on an axis pack beats a bridge pack wherever it
  suffices. The matrix is sparse — build only the bridges reality demands.
- **D-V10 — the plugin-on-plugin tier is the enabling mechanism**, designed per docs/22's
  enumeration: exported plugin contracts (the host-contract machinery, second provider),
  declared acyclic dependency edges with activation ordering + cascade/suspend +
  auto-activation, entitlement coupling, PLG010 relaxed only along declared edges. Layering
  stays depth two: host → axis packs → bridge packs.

## Phasing (each slice independently shippable)

1. **Profile facts**: `tenants.set-profile` + catalogs + manifest exposure + inheritance walk.
2. **Form overlays, tenant tier**: `tam.forms` package, closed mutation set minus validators,
   FRM001–005, per-tenant merge at the manifest route, admin grid.
3. **Fact-keyed pack presets**: `when` clauses, specificity merge, presets in plugin/package
   bundles (the nav-overrides-in-bundles deferral rides along).
4. **Validator sets + the structured address**: named sets in the model, manifest carriage,
   client pre-validation, the address superset on Order with `SE`/`NO` sets and country
   presets — the proving consumer, and the opening move of the order-location domain arc.
5. **Plugin-on-plugin (the dependency tier)**: plugin contract export (second provider of the
   docs/31 artifact machinery), `DependsOn` edges + activation ordering + cascade/suspend +
   auto-activation, entitlement coupling, PLG010's declared-edge relaxation. Independent of
   1–4; prerequisite for 6.
6. **The `se` country pack + one bridge**: ROT/RUT skeleton (deduction fields on order/invoice,
   eligibility rules, a mocked Skatteverket outbound) as the axis pack; a small `se ×
   electrical`-style bridge proving the intersection model and auto-activation end to end.
   The trade-pack convention proves out here too.

## Non-goals

- No per-tenant compiled models, no multi-model deployments.
- No form DSL or layout designer beyond the closed mutation set — demand must name the
  mutation before it joins the set.
- No workflow engine; flows stay rules + gates + entitlements until proven insufficient.
- No free-form profile facts; every fact value validates against a host-declared catalog.
- No dense variant matrix: nobody ever authors "the Swedish electrician edition" — tenants
  compose axes; bridges exist only where an intersection has real behavior.
