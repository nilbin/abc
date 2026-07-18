# 37 — Variability: it's just plugins

**Status: design; the plugin relationship model's `DependsOn` edge (both levels) is BUILT** —
see docs/22 "Plugin-on-plugin" and D-V4 below; proven by `fortnox` depending on `invoicing`.
`Provides`-one-of, `Conflicts`, and everything else here remain design. This doc answers the question the field-service domain forced:
*how can the product differ by the company's country, by trade (electrician vs plumber), and by
company size, without forking?* After three passes and a four-lens review round, the answer
collapsed to something smaller than it started: **variability is plugins at the right
granularity, composed per tenant, with the tenant-package tier as the onboarding convenience —
plus one genuinely new framework concept, the plugin relationship model.** Country and trade
are not framework concepts at all; they are emergent from which plugins are active.

Revision history: rev 1 tried to carry country as presentation overlays; rev 2 (correctly) made
a country a plugin but invented "axis packs" and "bridge packs" as new tiers and a form-overlay
registry; the review round showed those tiers and registries were phantom scaffolding for one
real consumer. Rev 3 (this doc) dissolves them into plugins + packages + relationships.

## The spine: one compiled model

There is exactly ONE compiled model per deployment. Variation is never conditional compilation,
never `if (country == "NO")` in domain code, never a per-tenant build. It is composed at runtime
from three things the framework already has — plugin **activation** (docs/22), plan
**entitlement** (docs/24), and tenant **data** (extension fields docs/15, automation rules P5) —
governed by one new thing: how plugins **relate** to each other.

## Variability is plugins at capability granularity

The reframe that made everything else fall away: don't build coarse country or trade packs;
build **one small plugin per capability**, and compose. Each of these is its own plugin:

- **Fortnox** — an accounting integration (already a plugin in the repo: `samples/fortnox`).
- **Skatteverket** — a tax-authority integration (submissions, reimbursement claims).
- **ROT**, **RUT** — the deduction domain features (packaged fields on order/invoice, labor
  shares, eligibility, caps). Separable from Skatteverket: the deduction can exist with manual
  filing; the integration serves more than deductions.
- **electrician** — the trade domain (materials catalog, checklist templates, nav suggestions).
- **SäkerEl** — an electrical-safety certification/inspection scheme gating `orders.complete`.

"Sweden" is not a pack — it is *whichever of these a Swedish tenant runs*. "Electrician" is not
a pack type — it is the electrician plugin plus whatever compliance plugins a given market
attaches. There is no country×trade **matrix** to build because nobody assembles a grid; they
compose from a library. The intersection cases that rev 2 called "bridge packs" are just more
small plugins that happen to depend on two others (below) — no special tier, no auto-activation
machinery. Usually the intersection isn't even code: ROT-vs-RUT-for-this-job is *which
deduction plugin is active and configured*, i.e. data.

This is the Odoo model precisely (hundreds of small modules; localization and industry bundles
assembled from them), and it lands on the three tiers docs/22 **already** defines — framework
packages, plugins, tenant packages — instead of inventing new ones.

## Packages: the retroactive onboarding bundle

Fine granularity has one real cost: a tenant admin should not face a hundred toggles. The answer
is the **tenant-package tier that already exists** (P3 — `InstallPackage`/dry-run/uninstall,
docs/22). A package is not code; it is a **curated bundle** — "activate ROT + RUT + Skatteverket
+ Fortnox + electrician + SäkerEl, with this preset configuration." One install, a configured
Swedish-electrician tenant.

The keyword is **retroactive**. You do not design the bundles up front — that is the matrix trap
(and the prior-art review confirmed such bundles proliferate and erode under commercial pressure
when planned ahead). You ship granular plugins, watch which combinations recur across real
onboardings, and mint a "Swedish electrician starter" package **once a real tenant-shape
justified it**. Packages are demand-driven curation — sparse by construction, because a package
only exists after the need showed up. A package can also be tenant-authored or partner-authored,
exactly as P3 already allows.

## The plugin relationship model (the one new piece)

At this granularity plugins genuinely relate to each other, so the framework must model the
relationships — not as a feature, but as a **guardrail** in the spirit of PLG010: it keeps a
composed set legal and refuses dangerous combinations before they silently misbehave. Three
edge types, all **declared** and verified at activation/build time:

- **DependsOn** — B requires A. Two levels, and only the first is needed early:
  - *Level 1 — activation dependency (no code coupling):* "ROT-submission requires Skatteverket
    active"; "the Swedish-electrician package requires electrician + SäkerEl." A declared edge
    checked at activation: refuse to activate a plugin whose dependency is absent; order
    activation so parents come first. Cheap; this alone makes granular composition safe.
  - *Level 2 — contract coupling:* a plugin that subscribes to another's events, reads its
    views, or gates its operations. This needs the `host-contract.json` machinery to emit
    **per-plugin** (docs/31's exporter generalized to a second provider), PLG010 relaxed to
    accept the declared edge, and the generated typed facades. Needed the moment the first real
    cross-plugin hook lands (ROT gating Skatteverket's submit; a checklist plugin reading
    electrician) — demand-triggered, not speculative.
- **Provides-one-of** — the model for **mutual exclusion**, stated positively. The host (or a
  plugin) declares a **singleton capability slot** — `accounting-sync`, `invoice.tax-treatment`,
  `order-line-structure`. Plugins declare `Provides("accounting-sync")`; the framework refuses
  to activate two providers of the same slot at one node. Fortnox and Visma both provide
  `accounting-sync` → pick one. Two country plugins that each want to own the invoice's tax
  treatment → pick one *per node* (a multinational group still spans countries, because
  activation is per-node). This beats raw pairwise conflict because the host controls which
  surfaces are genuinely singletons; everything additive and namespaced coexists untouched.
- **Conflicts** — the escape hatch for the rare exclusion the slot model doesn't capture: a
  declared pairwise "A and B cannot both be active."

**Chains, not a depth cap.** Rev 2 said "depth two by convention"; the review correctly dinged
that — real ecosystems reach depth three (a country×feature glue plugin on top of a country
plugin). The honest guardrail is **acyclic + every edge declared**, at any depth. The
contract-export mechanism generalizes to any number of hops; cycles are the thing to forbid,
not depth.

What stays **deferred** until a real consumer needs it: the heavy lifecycle machinery —
cascade/suspend on parent deactivation, entitlement coupling across the edge, auto-activation.
Level-1 edges + provides-one-of + conflicts are the guardrail worth having from day one; the
rest waits.

## Profile facts: onboarding hints, nothing more

Rev 1/2 made `country`/`trades`/`size` a first-class fact registry that drove overlays and
selection. The review showed every consumer was either derivable from activation or deferrable.
So facts shrink to their honest role: an **onboarding hint**. "The tenant said Sweden +
electrician" → *suggest* the Swedish-electrician package and pre-check the likely plugins. A
fact is read once, by host signup/onboarding code, to propose a package; it is **never**
authorization, never a runtime branch, never re-read by an entitlement gate. `country` is not
even needed as stored state once the country's plugins are active — it is implied by them.
Whether facts are stored at all, or just transient onboarding answers, is a build-time call;
either way they carry no load-bearing semantics.

## Presentation and the structured address

Most presentation variation belongs to the plugin that owns the capability: a plugin **declares
its own forms, ships its own locale catalogs, and validates in its own gate** (docs/22). A
country plugin's postal-code rule is a `RequiredWhen`/constraint in its own operation gate —
server-enforced, and (via the portable AST, docs/05) echoed client-side — not a separate
"validator set" abstraction. The EU e-invoicing CIUS pattern the review surfaced (a per-country
tighten-only subset of EN 16931) is real, and it is exactly this: the country plugin tightens
in its own gate over a host/feature superset invoice. No new registry.

The one gap is presentation over a **host** field a plugin cannot redeclare. The reference case
is the **structured address**: today one text field (`workAddress`); the real thing is a value
object whose shape is the **superset of every target country** — street, number, postal code,
city, region, country — declared ONCE in the host model (D4-additive: the text field retires,
structured fields arrive). A country plugin then varies only *presentation* over host fields
(hide `region` where meaningless, reorder postal-before-city, relabel). Tenant-authored form
tweaks ride the **existing** compiled-field overlay (docs/15 already relabels/hides-optional/
reorders/tightens compiled fields per tenant) — no new `tam.forms` registry. **Pack-shipped**
presentation over host fields is the one genuinely missing mechanism, and it is deferred until a
second consumer beyond the address names it; for the address alone, ship the country
presentation as host-model defaults keyed off the active country plugin.

The address is the acceptance test: it is the opening move of the parked order-location arc, so
building it pays twice — and if it needs anything beyond a host superset + a country plugin, the
model is wrong.

## Data lifecycle: the retention obligation (must-solve)

A blocker the review surfaced, independent of everything else: a plugin's **packaged fields**
store their spec in compiled plugin code, so deactivating or uninstalling the plugin makes its
data (e.g. ROT deduction amounts on years of invoices) unrenderable — and ROT is a
Skatteverket-auditable record with multi-year retention duty. Deactivation already preserves the
bytes ("hides, never deletes"), but not the *spec* needed to read/type/label/export them.

**Requirement:** packaged-field **retirement** — on deactivate/uninstall, persist a frozen
read-only spec tombstone (label + type) so orphaned `ext.{plugin}.*` data stays queryable and
exportable but immutable, mirroring tenant extension-field retirement (docs/15). Any plugin
writing regulated data must solve this before it writes to production. This attaches to the
individual plugin (ROT), not to any "country pack."

## Flows

"Flows differ by country" decomposes into pieces that all exist: approval rules (docs/24 wildcard
gates + samples/approvals) shipped by a plugin as presets; automation rules (P5) that mutate and
chain operations; entitlement/activation deciding which operations exist. No workflow engine. If
a real case ever demands sequencing the rules cannot express, that is a new design conversation.

## Decisions

- **D-V1 — one compiled model; variation = activation + entitlement + data + relationships.**
  Never per-tenant builds, never country/trade branches in domain code.
- **D-V2 — variability is plugins at capability granularity.** One plugin per capability
  (Fortnox, Skatteverket, ROT, RUT, electrician, SäkerEl); country and trade are emergent from
  the active set, not framework concepts and not pack tiers.
- **D-V3 — the tenant-package tier (P3) is the onboarding bundle**, minted **retroactively**
  when a tenant-shape recurs. No matrix of pre-built variants.
- **D-V4 — the plugin relationship model is the one new framework concept**: `DependsOn`
  (L1 activation dependency; L2 contract coupling), `Provides`-one-of singleton slots for mutual
  exclusion, `Conflicts` as escape hatch. Acyclic at any depth (declared edges, no cycles) — not
  a depth cap. Verified at activation/build. **`DependsOn` is BUILT at both levels** (PLG011 +
  the PLG010 relaxation over event/view/subscribe/outbound-trigger seams; L1 activation guards;
  per-plugin contract slices + generator facade merge), proven by `fortnox` → `invoicing`;
  `Provides`/`Conflicts` stay designed.
- **D-V5 — profile facts are onboarding hints only.** Read once to suggest a package; never
  authorization, never a runtime branch, never re-read by a gate.
- **D-V6 — a plugin owns its own presentation and validation** (declares its forms, ships its
  locale, tightens in its own gate — the CIUS pattern). Tenant form tweaks ride the existing
  docs/15 overlay. Pack-shipped presentation over HOST fields is deferred to a named consumer.
- **D-V7 — supersets over forks** (unchanged): a host entity that varies by country is declared
  once as the superset; a plugin varies presentation + tightens validation over it. The
  structured address is the reference case; order-line sections (German GAEB Titel/Positionen)
  are the pending structural test — default lean host-superset.
- **D-V8 — packaged-field data must survive its plugin.** Retirement tombstones (frozen spec,
  data preserved, keys reserved) are a must-solve for any plugin writing regulated data.
- **D-V9 — heavy plugin-lifecycle machinery is deferred**: cascade/suspend, entitlement
  coupling, auto-activation wait for a real consumer; the guardrail edges do not.

## Phasing (each slice independently shippable)

1. **The structured-address superset** on Order (host model, D4-additive) + country presentation
   as host-model defaults — the proving consumer and the order-location arc opener. Needs none
   of the machinery below.
2. **The plugin relationship model, level 1**: `DependsOn` activation edges, `Provides`-one-of
   singleton slots, `Conflicts`; activation-time verification + diagnostics; admin visibility of
   why a plugin is present/blocked. The guardrail.
3. **A real Swedish plugin set**: ROT (deduction packaged fields + eligibility + gate) as its
   own plugin, Skatteverket as its own integration plugin, related by a level-1 `DependsOn` —
   proving granular composition. Includes the packaged-field retirement tombstone (D-V8).
4. **Plugin-on-plugin level 2** (contract coupling): per-plugin contract export, PLG010
   declared-edge relaxation, generated facades — built when slice 3's first real cross-plugin
   hook needs it (ROT gating Skatteverket's submit).
5. **A retroactive package**: bundle the slice-3 plugins into a "Swedish electrician starter"
   via the existing P3 tier — proving the onboarding-convenience layer.
6. **Pack-shipped presentation over host fields** — only if a second consumer beyond the address
   names it. Otherwise never built.

## Non-goals

- No per-tenant compiled models, no country/trade as framework concepts, no pre-built variant
  matrix — tenants compose plugins; packages are curated after the fact.
- No new form-overlay registry, no separate validator-set abstraction — a plugin owns its forms
  and validation; tenant tweaks ride the existing docs/15 overlay.
- No profile-fact-driven runtime behavior; facts only ever suggest a package at onboarding.
- No heavy plugin-lifecycle machinery (cascade/suspend, auto-activation, entitlement coupling)
  until a real consumer demands it.
- No workflow engine; flows stay rules + gates + activation.
- No dropping of regulated data on plugin removal — tombstone, never orphan.
