# 37 — Variability: country, trade, size

**Status: designed, not built.** This doc answers the question the field-service domain forced:
*how can order surfaces, forms — and maybe flows — differ by the company's country, by trade
(electrician vs plumber), and by company size, without forking the product?* The answer is a
composition of three mechanisms the framework already has, plus one new registry that mirrors
nav v2 exactly. Nothing here invents a second model.

## The spine: one compiled model, three variation channels

There is exactly ONE compiled model per deployment. Variation is never conditional compilation,
never `if (country == "NO")` in domain code, never a per-tenant build. Every axis of variation
routes through one of three channels, each already proven elsewhere in the framework:

1. **Activation + entitlements** — *what capability exists here* (docs/22, docs/24). Plugins
   activate per tenant; plans entitle plugins and seats. This channel answers trade and size.
2. **Overlays** — *how a declared surface presents* (docs/30 nav v2 is the template). Registry
   data merged into the effective manifest per tenant, fingerprinted into the ETag, dormant when
   its target vanishes, retire-restores-the-default. This channel answers country and taste.
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
  shipped preset serves every matching tenant; a Norwegian country pack is mostly locale
  catalogs + form presets + validator sets + (later) tax integrations.

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

## Trade and size: the pack conventions

- **A trade is a plugin** (or a package of them). The electrician pack ships inspection
  checklist templates, a materials catalog, rule presets (e.g. certification checks gating
  `orders.complete`), nav suggestions, form presets. Install + activate = the product speaks
  electrician. Nothing in the host names a trade; `trades` facts at most drive install-time
  suggestions ("tenants like you activate …").
- **Size is a plan** (docs/24). Capability differences by size are entitlements at the anchor —
  never overlay tricks. Presentation simplification for small shops ("hide the project fields,
  one-person companies don't dispatch") is a pack preset `when: { size: "micro" }` — visual
  decluttering over the same capability set, honest to D-N6.
- **A country is locale + presets + validator sets** (+ eventually tax/e-invoice integrations
  on the docs/25 channel). Notably NOT a fork of any entity.

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
- **D-V7 — trade = plugin, size = plan, country = locale + presets + validator sets.**
- **D-V8 — supersets over forks**: an entity that varies by country is declared once as the
  superset of shapes; variation is presentation + validation selection. The structured address
  is the reference case.

## Phasing (each slice independently shippable)

1. **Profile facts**: `tenants.set-profile` + catalogs + manifest exposure + inheritance walk.
2. **Form overlays, tenant tier**: `tam.forms` package, closed mutation set minus validators,
   FRM001–005, per-tenant merge at the manifest route, admin grid.
3. **Fact-keyed pack presets**: `when` clauses, specificity merge, presets in plugin/package
   bundles (the nav-overrides-in-bundles deferral rides along).
4. **Validator sets + the structured address**: named sets in the model, manifest carriage,
   client pre-validation, the address superset on Order with `SE`/`NO` sets and country
   presets — the proving consumer, and the opening move of the order-location domain arc.
5. **The electrician pack**: a sample trade pack proving the convention end to end.

## Non-goals

- No per-tenant compiled models, no multi-model deployments.
- No form DSL or layout designer beyond the closed mutation set — demand must name the
  mutation before it joins the set.
- No workflow engine; flows stay rules + gates + entitlements until proven insufficient.
- No free-form profile facts; every fact value validates against a host-declared catalog.
