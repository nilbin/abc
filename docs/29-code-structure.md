# 29 — Code structure: the repo map and where things go

Status: **normative** for source layout. CLAUDE.md carries the working agreement; this doc is
the reference it points at. docs/16 is the design-era NuGet packaging sketch, not the source
layout — the two answer different questions.

## Repo map

```
src/Tam.Core               contracts, model builder + verification, manifest, portable AST,
                           locale catalogs. ZERO package references — BCL only. Types here are
                           wire-adjacent; adding a dependency here is a design event.
src/Tam.Compiler           Roslyn analyzer + source generator (TAM/L10N/EDIT/PLG rules,
                           AddDiscovered). netstandard2.0 — no modern C# runtime helpers.
src/Tam.EntityFrameworkCore EF integration: conventions, tenant filter/stamp, audit capture,
                           merge, framework ENTITIES, outbox/idempotency records, IsoTime.
src/Tam.AspNetCore         the pipeline (executors, endpoints, DI), background loops,
                           framework packages (Packages/), pipeline infrastructure (root).
src/Tam.Auth.OpenIddict    the embedded auth server behind IActorProvider.
src/Tam.Testing            the in-process test harness (tutorial Step 11): TamTestHost drives
                           the REAL executors against a real provider; CapabilitySweep executes
                           every declared view capability. References AspNetCore; sits outside
                           the app pipeline.
packages/tam-core          TS manifest types, Px evaluator, localization, HTTP client.
packages/tam-react         React runtime: context, renderers, OperationForm, ViewGrid.
                           Server state (view reads) goes through TanStack Query (useView);
                           a committed write's effects drive TARGETED invalidateQueries.
samples/web                   the sample SPA (Norrservice ERP) — host code, not framework.
samples/erp                the reference host: domain, bindings, seed, Program wiring.
samples/inspect|fortnox|approvals   vendor-plugin exemplars (the plugin shapes to copy).
samples/erp.Tests          the CONSUMER test suite: Tam.Testing over the real erp model
                           (pipeline scenarios + the capability sweep) — what a product team's
                           test project looks like.
tests/Tam.Tests            the unit/pipeline suite. Wire verification scripts live outside
                           the repo (session tooling) — STATUS records what they proved.
docs/                      numbered design docs; 19-decisions.md is the ledger; tutorial/
                           one page per step; index.md + llms.txt front the published site.
mkdocs.yml + .github/workflows/docs.yml   the docs SITE: MkDocs Material on GitHub Pages,
                           deployed on every push to main (STATUS.md becomes /status).
```

Dependency lines that must not blur: Core references nothing; EFCore references Core;
AspNetCore references both; nothing references a sample; samples reference the framework and
each other never.

## The package tier on disk

One package = one file: `src/Tam.AspNetCore/Packages/<Area>.cs`, containing in order

1. the `[TamPackage]` class (its `Configure` holds the form/grid bindings),
2. its `<Area>Findings`,
3. its gates / policy helpers,
4. its operations,
5. its views.

Registration and implementation are never in different files — that was the smell that
motivated this layout, and the authoring reshape (review round 4) finished the thought:
BEHAVIOR registration lives on the behavior itself ([Gate]/[OnEffect] attributes), and a
plugin that outgrows one Configure splits into explicit `IPluginPart`s
(`plugin.AddPart<OrdersContract>()`) — Configure stays the table of contents (the invoicing
sample shows the shape). A package that outgrows ~400 lines graduates to a folder under
`Packages/` and splits within it (the approvals sample shows the split shape: Domain /
Features / Plugin). The shared locale catalogs stay one embedded pair
(`src/Tam.AspNetCore/locales/{sv,en}.json`) — per-package locale files would fragment the
L10N001 gate for no benefit.

## Package content vs pipeline infrastructure

Litmus test: **"would this type still exist if the package's admin surface didn't?"** If yes,
it is infrastructure and lives at the project root, never inside `Packages/`.

The canonical infrastructure list and why (consumers in parentheses):

- `PluginActivation.cs` — `PluginActivations`, `ActivationCache`,
  `PluginAwareExtensionRegistry` (OperationExecutor, ViewExecutor, ResolveExecutor,
  manifest + integration endpoints).
- `Entitlements.cs` — `Subscriptions` + `SubscriptionFindings`: the enforcement half of
  billing (plugins.activate entitlement gate, users seat lease). A billing check can't live
  behind the activation it gates.
- `SecretVault.cs` — the encryption service (outbound integrations, DI, host seeds).
- Executors, outbox dispatcher, retry drivers, scheduler, janitor, SSE broadcaster,
  `TamActivator`, `PinnedScope`, `EnvelopeReplay`, `TamDirectory` — the pipeline itself.

The one sanctioned cross-package call: a package's public **policy helper** (e.g. `RoleRules`
— the single validation path for `roles.define` AND tenant-package install, so a bundle can
never become a privilege-escalation vector). Packages never call each other's operations.

## Where a new feature's pieces go

| Piece | Location |
| --- | --- |
| Entity | `src/Tam.EntityFrameworkCore` (`*Entity`, `ITenantScoped`, `*Json`, `*Iso`) |
| Operation / view | the owning package file (framework) or plugin Features.cs (vendor) |
| Form / grid binding | the same package/plugin `Configure` |
| Nav declaration | package/plugin: `plugin.Nav(...)` in `Configure` (content + suggestion); host layout: `model.Nav("web", …)` in Program.cs — never the other way (docs/30) |
| Finding codes | `<Area>Findings` in the same file; locale entries in BOTH catalogs |
| Gate / handler classes | INTERNAL top-level classes in the owning part/feature file, registered by their own [Gate]/[OnEffect] attributes (AddDiscovered picks them up; generated code cannot reach private nested types); ctor-injected |
| Test | `tests/Tam.Tests` — plus an analyzer rule when the invariant is structural |
| Wire change | re-export `manifest.baseline.json` + regenerate `samples/web/src/generated/tam.ts` |

## Namespace policy

Namespaces are API; folders are not. File moves never change namespaces as a side effect.
Current split: package classes + extensions/roles/audit content sit in
`Tam.AspNetCore.SystemOps`; the other packages' content sits in `Tam.AspNetCore` (the
`SystemOps` aggregate `AddTamSystem` is the public entry hosts call). Unifying these is
possible pre-1.0 but must be its own deliberate commit, never ride a refactor.

## Enforcement map

| Convention | Enforced by |
| --- | --- |
| Operation/view shape, authorize, tenant filters, query composition, paired atoms | analyzer TAM001–006 (build error) |
| No display text in code; label keys exist in default culture | analyzer L10N001 + Build()-time gate |
| Change<T> discipline | analyzer EDIT001 |
| Plugin/package namespacing, gate targets, packaged fields, plugin-only seams | Build()-time PLG000–005 / PKG000 |
| Wire-name permanence | CI baseline check (scripts/check_manifest.py) |
| Declared capabilities actually translate | Tam.Testing CapabilitySweep (samples/erp.Tests, runs in CI) |
| File layout: line cap (~400) + one wire prefix per file | CI structure check (scripts/check_structure.py; allowlisted exceptions carry a reason) |
| Naming, cross-package policy-helper rule | review + this doc — promote to analyzer when it hurts |

## Refactor ledger

Done:
- [x] Packages/ co-location (11 files), infra extraction (PluginActivation, Entitlements,
      SecretVault), InboundIntegrations rename — `dbdb9d8`.

- [x] Framework renderers into tam-react; badge-color registry; conflict-dialog locale keys;
      Tam.AspNetCore.Postgres extraction; MockFortnox to samples/fortnox; UseTamConventions +
      TamManifestExport host helpers; erp locale dedupe.

- [x] App.tsx rewritten onto nav slots (docs/30 v1): the hand-wired NavLink block and one-line
      page components are gone; what remains is the shell, `CustomerPicker`, and the one custom
      page (`OrdersPage`, registered via `registerPage`). Nav model + merge live in
      `Tam.Core/Nav.cs` + `TamModel.MergeNav/VerifyNav`; slot components in
      `packages/tam-react/src/nav.tsx`. (Since docs/34 M5, `CustomerPicker` is gone too —
      `[Lookup]` on the wrapper type renders the picker from the manifest; App.tsx is shell
      + badge colors only.)

- [x] The mechanical-split batch, proven by a byte-identical manifest AND an unchanged web
      bundle hash: `ModelConventions.cs` → conventions + `FrameworkEntities.cs`;
      `TamAspNetCore.cs` → `Providers.cs` + `TamServices.cs` (AddTam) + `TamEndpoints.cs`
      (MapTam, as one partial class — the public name is API); `TamModel.cs` → model +
      `TamModelBuilder.cs` + `ModelVerification.cs` (partial builder); `TamPasswords.cs` and
      `PipelineFindings` (→ `Findings.cs`) extracted; `tam-core/src/index.ts` → `manifest.ts` /
      `px.ts` / `i18n.ts` / `client.ts` + re-exporting index; `erp/Db.cs` → Db + `Seed.cs`;
      embedded-locale loads memoized per assembly (the 11 packages now deserialize the shared
      catalogs once). The invite-accept item was already satisfied — the handlers render via
      `AuthPages` and no inline markup remained.

- [x] `orders.overview` retired: the standard `orders.list` carries `SubtreeRead` (docs/26
      D-H1 evolved) — one view per aggregate again; roll-up is a capability, not a twin view.

- [x] `Tam.Core/Plugins.cs` (457) → `Plugins.cs` (identity tier) + `PluginHandlers.cs` (seam
      contracts + definitions) + `PluginBuilder.cs` (authoring surface);
      `TamModelBuilder.Seams.cs` partial extracted (the PLG005 plugin-only internals).

- [x] `NavOverlay` (+ its manifest-route application) out of `Packages/NavOverrides.cs` into
      `src/Tam.AspNetCore/NavOverlay.cs` — the litmus test above, applied to our own newest
      package (review round 4 #7). The package file keeps the operations/view/grid.

- [x] `ErpModel.Build()` extracted from Program.cs (samples/erp/ErpModel.cs): the model is a
      value consumed by BOTH hosts — Program.cs serves it, Erp.Tests verifies it (Step 11).
      Proven mechanical: manifest byte-identical.

- [x] `apps/web` → `samples/web`: the SPA is the sample HOST's frontend, not a product tier —
      the `apps/` split was day-one scaffolding convention, and the map above already said
      "host code, not framework". Mechanical (npm workspace path, CI paths, doc references);
      tsc + vite build green, output still `samples/erp/wwwroot`.

- [x] `ViewGrid.tsx` split on the M6-triage touch: `GridFilters.tsx` (the per-wire-kind
      filter control set) and `badges.tsx` (badge color registry + `registerBadgeColors`,
      re-exported from the index unchanged) extracted; proven mechanical by an IDENTICAL
      web bundle hash and a clean full tsc.

- [x] RETIRED without code — "wildcard-gate set caching": the ask predated the authoring
      reshape; `ActivationCache` (request-scoped, per-tenant memo, one query per request)
      already collapses the gate path's activation lookups, and the remaining per-execution
      work is a list concat. Nothing left worth a cache.

- [x] RETIRED without code — "packaged-writer unification onto the operation path": the
      writer now independently enforces every invariant unification was meant to buy —
      declared-field structural check (PLG010), the same semantic validation as the wire
      channel, an explicit tenant re-check over FindAsync (review-round-4 F1),
      plugin-attributed audit in the same transaction, and backplane refresh. Routing it
      through the operation executor would add idempotency/gate machinery that does not
      apply to subscriber-driven writes, for no additional invariant. If a future invariant
      lands on the operation path, this decision is the first to revisit.

Open: nothing — new debts get a checkbox here when they appear.

## Conventions — the written rules (beauty arc 1)

The beauty review's core observation was precedent-by-imitation: the same shaped problem
solved slightly differently per file because the rule was never written down. These are the
rules; deviations shipped before this section are grandfathered by D4 wire permanence and
noted where they matter. Every rule that CAN live in code does — the doc names the authority
class, and the class is the rule.

**Wire ids** (permanent once shipped — D4):

- Operations: `{surface}.{verb}` with a kebab facet when one verb has flavors —
  `orders.edit-details`, `extensions.define-field`. The verb vocabulary: `define` = upsert by
  natural key; `set` = singleton upsert; `create`/`edit-*` = entity lifecycle; intents are
  named for the business act (`complete`, `retire`, `approve`).
- List views: `{surface}.list` unless a package genuinely has several lists (then plural
  nouns: `audit.entries`, `integrations.runs`).
- Form ids: `web.` + the operation id. Grid ids: `web.{surface}`.
- Grandfathered deviations (live ids, never renamed): `web.extensions.define`,
  `extensions.fields`, `nav.overrides`, `stock.edit`.

**Names tenants author** — one shape per kind, one authority (`Naming`, Tam.Core):

- Slugs (rule names, role names, tenant ids): `Naming.IsSlug` — lowercase words joined by
  hyphens. Each surface refuses with its OWN finding (`rules.invalid-name`,
  `roles.invalid-name`, `tenants.invalid-id`) so the message can teach the shape.
- Extension-field keys: `Naming.IsCamelKey` — used identically by `extensions.define-field`
  and the package-install path (the RoleRules lesson: one rule set, two doors).

**Label keys** — minted only through `LabelKeys` (Tam.Core): `LabelKeys.Field(member)`,
`.OperationTitle(id)`, `.IntegrationTitle(id)`, `.Nav(id)`, `.Extension(key)`. A `[LabelKey]`
attribute appears ONLY when it deviates from the convention default — an attribute that
restates `labels.{kebab(member)}` is noise and gets deleted. Storage encodings never leak
into label keys (`NextRunIso` labels as `labels.next-run`).

**The tenant word**: framework catalogs say *organization* (`labels.tenant`, `nav.tenants`,
the `tenants.*` messages). A host that sells another word ("company", "site", "клиника")
overrides those keys in its own locale files — that is what the override layer is for, and
samples/erp does exactly this to keep its "Bolag" branding.

**Findings**: every stable code lives in a `*Findings` class on its surface; call sites use
the implicit factory→finding conversion. Non-operation endpoints answer through
`TamEndpoints.FindingsResult` — one envelope shape, one code→status mapping, no hand-built
anonymous error objects at the edges.

**Activation**: "does this contribution exist for this tenant?" is asked ONLY through
`ActivationCache.ContributionExistsAsync` (packages always active, hosts always exist).
No call site re-derives the union.

**TenantId stamping**: ambient (the save interceptor) for anything running in the operation
pipeline; explicit only for seed and background writes. An explicit stamp inside a pipeline
operation is a bug-shaped comment magnet — don't.
