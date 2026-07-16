# Implementation Status

Built overnight as a working vertical implementation of the design docs, validated end to end by
the Norrservice ERP sample. This file is the honest ledger: what runs, what's verified, and where
reality still falls short of [docs/20-tutorial.md](docs/20-tutorial.md).

## What runs

```
src/Tam.Core                 contracts, findings+args, Change<T>, semantic types, portable AST,
                             model builder, manifest, locale catalogs (L10N001 gate at startup)
src/Tam.Compiler             Roslyn analyzer: TAM001-006 (model shape, tenant filters, query
                             composition, paired-atom scoping) + L10N001/EDIT001 as build errors
src/Tam.EntityFrameworkCore  three-way merge, field-level audit + inferred effects, idempotency,
                             ExtensionData JSON column, tenant field registry storage
src/Tam.AspNetCore           execution pipeline, view executor, batched resolve, manifest +
                             OpenAPI + MCP endpoints, outbox dispatcher, SSE broadcaster, plugin
                             system, and the framework admin surface as TWELVE always-active
                             [TamPackage] modules (users/roles/audit/tenancy/rules/nav/... with
                             their forms, grids and embedded sv/en locales)
src/Tam.AspNetCore.Postgres  the Postgres LISTEN/NOTIFY SSE backplane + the RLS backstop
                             (TamRls: policies over every tenant-scoped table, scope-synced
                             session settings — docs/33)
src/Tam.Auth.OpenIddict      embedded OpenIddict token server + ClaimsActorProvider (the
                             framework's own auth, behind the IActorProvider seam)
packages/tam-core            manifest types, portable AST evaluator, localization, HTTP client
packages/tam-react           context/renderers/OperationForm/ViewGrid modules + renderer
                             registry, Mantine renderer pack
samples/erp                  Customers/Projects/Orders + extension/plugin/package/rule/user
                             admin, sv+en locales, seed (users, subscription)
samples/inspect              inspection-checklists plugin (packaged field, gate, subscriber)
samples/fortnox              a plugin whose whole job is one inbound integration
samples/approvals            Step 16: nested approver groups + tenant-configured rules gating
                             host operations via the wildcard gate, park, replay seams
samples/invoicing            Step 17: extends the Orders domain — grid action contribution,
                             packaged-field writer, declared host-view reads (docs/31)
samples/web                     Norrservice ERP web app (Vite + React + Mantine)
tests/Tam.Tests              149 tests: merge, extension applier, Change<T> JSON, portable AST,
                             localization, auth/entitlements, plugin build validation, schedule
                             specs, reserved permissions, SSRF egress policy, approvals seams
                             (wildcard gates, park-across-rollback, envelope replay), nav merge
                             + NAV diagnostics + tenant overlay, subscription anchors, package tier
```

### Run it

```bash
# API + built web app on http://localhost:5100
cd samples/erp && dotnet run

# Frontend dev loop (proxies /api to :5100)
npm install && npm run dev:web

# Rebuild the shipped bundle into samples/erp/wwwroot
npm run build:web

# Tests
dotnet test
```

Manifest: `GET /api/manifest` · MCP endpoint: `POST /api/mcp` (initialize / tools/list / tools/call).

## Verified end to end (the tutorial's promises)

- **Views/grids**: join + declared-capability sort + paging; semantic wrappers as wire primitives.
- **Reactive create form**: portable `VisibleWhen/RequiredWhen` evaluated client-side from the
  manifest AST (project fields appear the instant "Projekt" is chosen); customer selection triggers
  batched server resolve → project options load, credit-block warning appears, work address is
  suggested and applied under `RecomputeIfUntouched`.
- **Conflict-safe partial edits**: exact tutorial wire behavior — non-overlapping stale-base edits
  merge cleanly; same-field edits return the structured conflict (original/current/submitted) with
  keep-current / use-mine in the UI.
- **Tenant custom fields**: seeded `machineSerialNumber` + `extensions.define-field` operation
  (registry checks EXT005/EXT006 style at definition time); field appears in create form, edit
  form, grid column, audit, and MCP schema with zero deploys; extension changes ride the same
  `Change` channel, merge three-way, and are constraint-validated.
- **Pipeline**: authorization gate, structural validation from nullability + semantic types,
  transaction, same-transaction field-level audit with inferred effects, idempotent replay by key,
  culture resolution.
- **Localization**: zero display text in code; sv/en catalogs; the L10N001 startup gate actually
  fired during development (missing keys → refused to boot) and the whole UI language-switches
  live, including tenant field labels and finding messages with args.
- **MCP**: 15 tools generated from the model; `*_resolve` preflight returns missing/required/options/
  warnings for partial input — agents hit the identical pipeline.
- **Build-time diagnostics**: the analyzer turns a missing [Authorize], missing Execute, a
  label key absent from sv.json, an enum exposed via Change<T> (EDIT001 — state transitions
  belong to intent operations), or a manual `TenantId ==` filter (TAM004 — tenant scoping is the
  global query filter's job; use `IgnoreQueryFilters()` for a deliberate cross-tenant read) into
  compiler errors. TAM004 immediately caught a real straggler the regex sweep missed, and the
  framework projects dogfood it. Tenant assignment is likewise automatic — a `SaveChanges`
  interceptor stamps `TenantId` on inserted `ITenantScoped` rows from the ambient tenant, so
  operations never write it by hand (verified on the wire for framework and domain entities).
- **Authorization (D1, first layer)**: role-based actors (admin/dispatcher/viewer via X-Demo-Role),
  pipeline 403s with localized findings, actor permissions in the manifest overlay, and the UI
  hides ungranted actions (verified: viewer sees no create/complete/custom-fields surfaces).
- **Live refresh (D5), now cross-instance**: committed effects broadcast over `/api/events` SSE;
  grids subscribe and auto-refresh debounced (verified: subscriber received entity-modified during
  an edit). Fan-out is behind `IEffectBackplane` — in-process by default, a Postgres `LISTEN/NOTIFY`
  adapter (`AddTamPostgresBackplane`) for multi-node so a grid on instance B refreshes from a commit
  on instance A. Verified on Postgres end to end: an SSE client received the effect via
  `NOTIFY→LISTEN→Deliver`, and a separate `LISTEN`er (standing in for another node) received the
  app's `NOTIFY` on commit. The duplicate SSE send (event went out inline *and* via the outbox) is
  gone — the outbox now owns only durable consumers (subscribers, outbound integrations).
- **Roles as tenant data (D1 back half)**: roles live in the database, managed via `roles.define`
  which validates grants against the compiled permission catalogue at definition time (typo'd
  permission → localized finding); a runtime-defined role works as an actor immediately.
- **Record scopes — the PAIRED-ATOM pattern** (docs/28, superseding D1's `:own` suffix): the base
  atom (`orders.read`) is own-scoped by default and a declared widening atom
  (`[Widens("orders.read-all")]`) lifts it — views scope with
  `.ScopedUnless(context, "orders.read-all", x => x.AssignedToActorId)`, operations re-check with
  `CheckOwnershipUnless`, levels expand `X-all` on X's tier, and TAM006 enforces BOTH directions at
  compile time for views AND operations (undeclared atom at a call site; unscoped view/operation
  over a widened resource — verified fired on all: the write-side operation hole a foreign id would
  drive is now a build error too). A review round hardened three edges: the reserved-atom carve-out
  (docs/24) covers the `-all` twin — `Actor.IsReserved` blocks `subscriptions.manage-all` from
  wildcard grants, level expansion and `roles.define`; PLG001 validates `[Widens]` atoms against
  the plugin namespace so a plugin can't mint a host widening atom; the analyzer reads the atom by
  position (not first-literal) and records nothing for a non-literal atom (fail-closed, no false
  (b)). Verified on the wire (13/13, SQLite + PG): the technician sees own rows on every
  read surface — list, overview AND detail, the last two fail-open under the old suffix model —
  and is rejected editing/completing foreign orders (edit had no check at all before); dispatcher
  (-all atoms), viewer (level `view` ⇒ read-all) and admin (`*`) see all; roles.define grants the
  widening atom and rejects the retired `:own` suffix.
- **D4 baseline in CI**: `manifest` export mode + committed baseline + additive-only checker;
  removed members, type changes, optional→required flips, and new required inputs fail CI until
  the baseline is consciously re-committed. GitHub Actions runs build/tests/baseline/frontend.
- **Idempotency hardened**: replay verifies a payload hash; same key + different payload is
  rejected (`pipeline.idempotency-mismatch`), verified on the wire.
- **Typed TS client**: `scripts/generate-types.mjs` emits per-operation input/output interfaces,
  view row/query types, and a `TypedTamClient` from the manifest (outputs now in the manifest);
  CI fails if the committed generated file drifts from the baseline.
- **OpenAPI 3.1** at `/openapi.json`, derived from the model: localized summaries, required from
  nullability, enum values, change-set schemas, 403/409/422 finding responses.
- **Audit as a read model (D3)**: `audit.entries` view + History page (admin-only nav) showing the
  field-level trail — timestamp, operation, actor, entity.field, old → new — straight from the
  same-transaction audit tables.
- **Mechanical filtering (D7)**: `Filterable(field)` composes typed SQL predicates over the
  projection and renders grid filter controls — no Query-record members, no per-view Where.
  One declaration yields every operator the field's type supports: equality, `field.from`/
  `field.to` inclusive ranges (dates, numbers, ordinal strings — lifted comparisons, so null
  cells fall outside every range; `string.Compare` for string-backed wrappers), and
  `field.contains` substring. Grid controls derive from the same wire kinds (date/number range
  pairs, contains inputs, yes/no selects); all operators verified on the wire on SQLite AND
  PostgreSQL, malformed values → 422. Tenant extension fields filter via `ext.{key}`
  (canonical-JSON containment over the converted column); Query records carry only Search.
- **Async reference lookup**: `LookupSelect` in tam-react — typed text becomes a debounced
  server-side search against a lookup view (`customers.lookup?search=…` verified on the wire);
  the option list never preloads the table, and the current selection stays visible. The app's
  CustomerPicker is now three lines of wiring.
- **Grid totals**: the record count is rendered from the localized `grid.total` catalog entry
  ("{count} träffar" / "{count} records") — `translate()` now resolves args like finding
  messages do.
- **Plugin packaging (docs/22 P1)**: `[TamPlugin("inspect")]` + `AddPlugin<T>()` registers a
  compiled, namespaced module (PLG001 enforces the id prefix on every contributed operation/
  view/form/grid and permission at model build); the samples/inspect plugin ships its own
  entity (host opts storage in with one `AddInspect(modelBuilder)` line), operations, view,
  bindings and embedded sv/en locales, discovered by its own (now-internal) generated
  `AddDiscovered()`. **Activation is tenant data**: `plugins.activate`/`deactivate` framework
  operations + `plugins.list` admin view; the effective manifest, MCP tool list and OpenAPI
  omit inactive plugins entirely, and their operations/views/forms answer 404 pre-authorization.
  Verified on the wire: 404 → activate → create/pass checklists (audited, D7-filterable,
  SSE-refreshed) → MCP tools appear → deactivate → 404 again → reactivate → data intact. In
  the web app the "Besiktning" nav entry and plugin page render purely from `manifest.plugins`
  + grid plugin tags — no app code names the plugin.
- **Plugin depth (docs/22 P2)**: three seams, all verified on the wire and gated per tenant.
  *Packaged fields*: `plugin.ExtensionField("order", "requiresInspection", "boolean")` joins
  the effective overlay through a plugin-aware registry wrapper — key-prefixed, labels from
  the plugin's locale files (`ext.inspect.requiresInspection`), validating/persisting through
  the same Change channel and appearing in forms, grid columns, MCP schemas alongside tenant
  fields with zero new downstream code. *Gates*: `plugin.Gate<ChecklistGate>("orders.complete")`
  runs declared preconditions after validation, before the handler — the gate CLASS is
  constructed per invocation with ctor injection (`ITamDb tam` as a parameter, no service
  locators), reads wire input and the plugin's own data, never host CLR types; manifest shows
  `gatedBy: ["inspect"]`; verified: unpassed linked checklist → 422 localized finding, pass →
  completes. *Effect subscribers*: `plugin.OnEffect<OpenFollowUpChecklist>("order-completed")`
  runs post-commit off the outbox in a tenant-pinned scope
  (isolated failures, at-most-once), verified: completion auto-opens a follow-up checklist;
  none of the three fire for tenants with the plugin inactive. PLG002/PLG004/PLG005 validate
  gate targets, packaged-field entities/types and plugin-only registration at model build.
- **Tenant packages (docs/22 P3)**: `packages.install` takes the bundle document (fields +
  roles), validates every item with the registry's own rules, and applies all-or-nothing in
  the pipeline transaction; `dryRun: true` runs identical validation and answers "what would
  this do" without applying. Verified on the wire: broken package → localized EXT findings and
  nothing applied; good package → field in manifest with labels, package-defined role usable
  immediately; identical re-install → no-op; downgrade → `packages.older-version`; conflicting
  redefinition → `packages.field-conflict`; `packages.uninstall` retires the package's fields
  (data and keys preserved) and drops the installation row; `packages.list` is the admin view.
- **Automation rules (docs/22 P5, v1 = validation rules)**: `rules.define` stores a trigger
  operation + a Px-AST condition (structured JSON, never a parsed string — user data only ever
  lands in const nodes) + tenant-authored per-culture messages. Definition-time diagnostics:
  RUL001 unknown operation, RUL002 unknown condition/target field (checked against input wire
  names AND the live ext.{key} overlay), RUL003 missing default-culture message. The pipeline
  evaluates active rules against the wire input before the handler; a firing rule fails the
  operation with `rules.{name}`, the tenant's message in the request culture, targeted at the
  declared field. Verified: a condition spanning a package-installed extension field and a
  compiled field (class-2 cold chain without a date → localized 422; with date or class 1 →
  passes; retired → stops). `rules.retire`/`rules.list` manage the registry.

- **Typed extension predicates (docs/15's "real JSON translation" — P4's main prerequisite)**:
  `ext.{key}` filters now do real JSON extraction through two owned DbFunctions with
  per-provider translations (SQLite `json_extract`, PostgreSQL `jsonb_extract_path_text` with
  a numeric cast); the operator set derives from the declared spec's wire kind exactly like
  compiled fields — exact equality (replacing containment matching), `contains`, ordinal
  ranges for strings/ISO dates, and true numeric equality/ranges (`ext.weightKg.from=100` —
  double-typed, so SQLite compares REAL, not TEXT). Grid controls render mechanically for
  extension fields by wire kind. Verified on SQLite AND PostgreSQL, including the
  `from=1000`-excludes-380 text-compare trap, malformed numbers → 422, undeclared keys ignored.

- **Auth (Tam.Auth.OpenIddict)**: embedded OpenIddict server with **Authorization Code + PKCE +
  refresh** for humans (a framework-rendered, localized login + tenant picker at /connect/authorize)
  and **client credentials** for machines — no password grant (OAuth 2.1). Access tokens are
  short-lived (10 min) and renew silently via a rotating refresh token (`offline_access`). Platform-global
  accounts (docs/26): the token subject is the account id; the chosen tenant rides a `tam:tenant` claim
  that `ClaimTenantProvider` turns into the request's scope, and `ClaimsActorProvider` resolves grants
  fresh from that tenant's membership each request (an account with no membership there gets none —
  the cross-tenant guard). Users are account+membership through users.define/deactivate/list; PBKDF2
  hashing. IActorProvider stays the seam for any external IdP. **The PKCE + refresh mechanics live in
  the framework client, not the app**: `@tam/core` `TamAuth` (redirect, callback exchange, token
  storage, bearer wiring, silent refresh) + `@tam/react` `useTamAuth` hook; `TamClient` retries a 401
  once after an automatic refresh. Verified end-to-end (curl + browser): framework login, tenant picker
  (Alva a member of two unrelated tenants), PKCE enforced (wrong verifier → 400), refresh grant issues
  a working rotated token, SPA stores + renews it, cross-tenant switch (demo → 5 orders/5 customers as
  admin; demo2 → 0 orders/2 customers as viewer), client credentials (mcp-agent), anonymous/insufficient
  → 403, tekla's :own scope through real tokens, reload keeps the session, login/logout UI.
- **Hierarchy capability cascade (docs/26 D-H5 + docs/27)**: role assignments on a membership carry a
  per-role `cascade` flag (`[{"name","cascade"}]`; legacy flat `["name"]` reads as cascade: false).
  `ClaimsActorProvider` walks the active node's ancestor chain (materialized `Path`): the active
  node's membership contributes all assignments, ancestors only cascading ones, and each membership's
  role names resolve against **its own node's** role definitions (cross-level resolution, unfiltered
  load) — still collapsing to one flat permission set, so `Actor.Can`/manifest/UI gating are
  untouched. The authorize endpoint accepts standing at any **descendant of a cascading membership**
  (segment-safe path-prefix test — "demo" is not an ancestor of "demo2"). Verified on the wire: Alva
  (cascading admin at demo, NO membership at child "nord") mints a nord token and has admin grants
  there while seeing ONLY nord's data (1 order/1 customer, no demo bleed; demo shows 5 orders, no
  roll-up — reads stay strict by default); tekla (non-cascading) requesting nord falls back to demo;
  legacy flat roles unchanged. Grants fan out, data stays per-node.
- **Hierarchy read scopes + act-as writes (docs/26 D-H1/D-H4 + docs/27)**: the global filter stays
  STRICT; a view widens explicitly — `InSubtree` (downward roll-up via a tenants-table semi-join) or
  `WithInherited` (upward shared read via a bounded ancestor IN-list); rows carry only TenantId, no
  path denormalization, so re-parenting rewrites the tenants table only. Sample: `orders.overview`
  (subtree, labeled by company) and customers as the group's shared registry (inherited, list/lookup/
  rules/derivations all widened together). Cross-node writes: the `X-Tam-Tenant` act-as header names a
  target node, validated against the account's standable set (membership or cascaded descendant;
  denied → 403 `tenants.not-standable`) and REBOUND as the request's ambient tenant — one resolution
  seam (`TamTenant.Resolve`) feeds the context, actor, filter, stamp, audit, outbox/effects,
  idempotency and lookups, so everything lands coherently in the target. The login tenant picker
  offers the full standable set, cascaded descendants labeled by path ("Demo AB ▸ Norrservice Nord
  AB"). ⚠ Composition rule (found on the wire): EF's IgnoreQueryFilters is QUERY-WIDE — a query
  composing a widened source must explicitly scope every other ITenantScoped source (`InNode`), or
  the join silently drops the strict filter; documented in docs/27, TAM005 candidate. Verified on the
  wire: overview rolls up 6 orders across both companies while orders.list stays at 5; nord sees 6
  customers (1 own + 5 inherited) with no upward leak; act-as create from demo lands the order, its
  audit entry and its numbering in nord (demo untouched); tekla/unknown-node act-as → 403; alva's
  picker lists demo, demo▸nord, demo2. 82 tests; baseline + typed client regenerated (orders.overview).
- **Capability model (docs/27 D-A1 + D-A3)**: roles are authored as ACCESS LEVELS per resource
  (`{"orders":"manage"}`) alongside explicit atoms — the catalogue is derived mechanically from every
  [Authorize] permission; levels expand to atoms at LOAD time (a new action flows into existing
  Manage roles); reserved atoms are never expandable. FIELD MASKING: `[Sensitive("customers.sensitive")]`
  on a view/input field gates it behind an atom — read masking removes the field from the manifest AND
  from view rows (the column does not exist for that actor); write masking rejects any input carrying
  it (pipeline.field-not-authorized). The mask atom joins the catalogue, so Manage grants it while
  View/Edit don't. Verified on the wire: vera (level-authored viewer) gets customers without
  email/phone in rows and manifest while alva ("*") sees values; didrik's create WITH email is
  rejected at the field and passes without it; {customers:manage} grants the atom (stina reads and
  writes email); {subscriptions:manage} still cannot reach set-plan. 82 tests; baseline + types regen.
- **Tenant lifecycle (docs/26)**: `tenants.create` creates a node as a CHILD OF THE ACTIVE node
  (writes fan in — a grandchild means acting-as the child first, like its data); the id is a path
  segment (lowercase, no dots, globally unique) and a pre-hierarchy tenant self-heals its root row
  on first child creation; no membership row is written — a cascading membership above reaches the
  new node immediately. `tenants.move` re-parents a strict descendant of the active node under the
  active node or another of its descendants — never the node you stand on, never out of your
  subtree, never into the moved subtree (cycle) — by rewriting the moved nodes' `Path` values in
  the tenants registry and NOTHING else. `tenants.list` is the active node's subtree. Verified on
  the wire (11/11): create `syd` under demo → path `demo.syd`, alva's standable set includes it
  live via cascade, act-as syd creates and reads a customer, invalid/duplicate ids rejected, move
  syd under nord → `demo.nord.syd` with data intact, cycle and out-of-subtree moves rejected with
  localized findings, viewer denied by the capability gate, overview roll-up unaffected. ADMIN UI:
  a Companies page (web.tenants grid + create/move toolbar forms, nav gated by tenants.read) and an
  Access-policies page (web.policies grid; the define form authors the resource→scope map with an
  app-owned "scope-map" renderer — rows of resource + all|own toggle). Verified headless: both pages
  render, the create form submits and the grid live-refreshes with `demo.syd`.
- **Roles admin page**: web.roles grid with the define form on the toolbar — access levels
  authored through a "level-map" renderer (resource + view|edit|manage; the same generalized
  keyed-choice editor as scope-map) and explicit atoms through string-list. DefineRole.Permissions
  is now optional, so a levels-only role is authorable (docs/27 D-A1: levels AND/OR atoms).
  Verified headless: a levels-only role ({"orders":"edit"}) defined through the UI lands in the
  grid.
- **Users admin page**: web.users grid (username, display name, roles, active; deactivate as a
  row action) with the invite form on the toolbar — roles/policies authored through an app-owned
  "string-list" renderer. Verified headless: an invite submitted through the UI lands and the
  grid live-refreshes with the new member.
- **Assignment & grouping settled AND BUILT** ([docs/28](docs/28-assignment-and-grouping.md),
  D-AG1…D-AG4): framework row scopes are the tenancy dimension only (the framework must own BOTH
  ends of a scope; tenancy — tree position + stamped TenantId — is the only such dimension).
  Ownership is the paired-atom capability pattern (see the record-scopes entry); `where`/`shared`
  are domain patterns (assignment tables keyed by actor id, one predicate enforced on both read
  and write); generic groups, if ever, arrive as one more source in the actor-resolution union
  (profiles → flat groups → nesting never in core); approval flows are plugin territory.
- **The three approvals seams are BUILT** (docs/28 D-AG4, tutorial Step 16): a wildcard gate
  (`GateDefinition.Wildcard`) runs on every operation with `gate.OperationId` + the pipeline's
  payload hash, so its target set is the plugin's own config; `gate.Park<TWork, TState>(state)`
  keeps a blocked envelope — domain transaction rolls back first, then the parked-work class is
  CONSTRUCTED in a fresh tenant-pinned scope (its injected `ITamDb` cannot be the rolled-back
  one, structurally), and work parked by an ALLOWING gate is discarded; `EnvelopeReplay` re-executes a
  parked envelope through the full pipeline as the original initiator (grants re-resolved as of
  now, fail-closed on a deactivated account), marked `InvocationSource.Workflow`, envelope id as
  audit `CorrelationId` + initiator-scoped idempotency key — dual attribution, replay-safe under
  redelivery. Six pipeline-level tests prove all of it on SQLite.
- **The sample-app/framework boundary is clean** (audit-driven): the three renderers framework
  packages reference (`culture-text`, `level-map`, `string-list`) moved into tam-react's default
  pack — framework admin forms no longer depend on app code in any host — plus a `money` form
  renderer and grid honoring; grid badge colors are a registerable map (domain enum colors left
  the library); the conflict dialog's hardcoded sv/en strings became locale keys; the Postgres
  backplane is `Tam.AspNetCore.Postgres`; the Fortnox mock lives with the fortnox sample
  (`MapMockFortnox`); host boilerplate absorbed (`UseTamConventions` for the tenant-stamp
  interceptor, `TamManifestExport.TryHandle` for the D4 export CLI); 16 erp locale keys that
  shadowed identical framework defaults deleted. Verified: web bundle builds, outbox → outbound
  → moved mock receives the voucher push end to end, full wire matrix green.
- **Subscriptions understand the tenant tree — the ANCHOR model** (docs/24 hierarchy, D-S1..6):
  a subscription row covers its subtree; the nearest ancestor-or-self anchor governs
  (`Subscriptions.CoveringAsync`, the grants chain walk applied to money); an anchor-less tree
  shares ONE free default anchored at the root — creating child nodes no longer mints fresh free
  seats (the ceiling bypass is closed). Entitlement is the anchor's, activation stays per node;
  seats POOL at the anchor with the lease on the anchor's row (cross-node invite races now
  conflict); sub-anchors (set-plan at a non-root node, billing-provider-only via the reserved
  atom) carve genuinely separate billing with absolute boundaries — no unions, no borrowing.
  `tenants.move` across anchor boundaries succeeds with entitlement-lost/seat-overflow WARNINGS
  (never destructive; reconciliation enforces). Wire: `subscriptions.current` gained
  `anchorTenantId` + pooled `seatsUsed` (additive). Verified on the wire: nord resolves demo's
  standard plan and anchor, identical pool from both nodes, child activates via anchor
  entitlement, seat-lease race and ceiling regressions green. 106 tests.
- **Navigation is manifest-driven — nav v1** (docs/30 D-N1..8): the compiled model carries a
  declared tree (mode → section → page, ids D4-permanent, labels by `nav.{id}` key); packages
  and plugins contribute CONTENT plus a suggested section slug while the host alone declares
  layout (`model.Nav("web", …)`, NAV000 stops a plugin trying) — sections collect matching
  suggestions in order-then-declaration sort, `Place()` adopts a contribution but the HOST's
  order wins, and anything uncollected lands mechanically under a well-known `more` section in
  the last mode (a plugin that declared nav has graduated and never also gets the generic
  fallback page — both semantics locked by tests the first cut actually failed). Diagnostics
  NAV000-005 at Build(); every nav label key is L10N001-gated. The manifest gains `nav` (per
  surface, activation-filtered per tenant) + `packages`; tam-react ships `NavProvider`/`useNav`,
  `NavModeSwitcher`/`NavSidebar`/`NavTabs`/`NavPage` (depth→slot) and `registerPage` for custom
  page targets; App.tsx is now just the shell composed of slots — the hand-wired NavLink block
  and one-line page components are deleted, all eight framework admin pages ride package nav
  declarations. Verified: 9-check wire suite (tree shape, suggestion collection, fallback
  tracking each tenant's OWN activation set — nord sees only inspect where demo sees
  approvals+inspect, catalog coverage in both cultures) plus rendered-UI screenshots of both
  modes and the fallback plugin page. 117 tests; manifest change additive (nav + packages).
- **Cross-domain plugins — Step 17 BUILT** (docs/31 D-X1..3, decisions ledger D9): a plugin
  now extends a domain it doesn't own. `plugin.GridAction(gridId, operationId, bind)` puts the
  plugin's operation ON the host's grid with a DECLARED input↔column bind (PLG006: grid exists,
  operation is the plugin's own, bind names real fields; manifest `contributedActions` is
  additive and activation-filtered; the client renders them behind the same permission gate and
  routes through the subtree per-row act-as). `IPackagedFieldWriter` is the write half P2 never
  had: plugin-scoped structurally (the pipeline stamps `PluginContext` around every handler
  construction — including the operation's own handler, which must CLEAR a prior gate's stamp:
  the wire suite caught tam.rules' wildcard stamp leaking into the invoicing handler),
  prefix+declaration enforced at runtime against the compiled model, semantically validated,
  audited with the plugin as actor, live-refreshing. `plugin.RequiresView(viewId, fields)` makes
  read compatibility a Build() fact (PLG008) and whitelists SERVICE-MODE reads
  (`IHostViewReader`: actor mode in requests, declared-only synthetic actor in effect handlers).
  `samples/invoicing` is the fourth exemplar and tutorial Step 17: create-from-order via the
  contributed button (actor-mode read validates + denormalizes), draft-on-completion subscriber
  (service-mode backfill), draft-pending gate on orders.complete, invoiceStatus riding the host
  grid as column+filter with plugin-attributed audit. Verified: 125 unit tests (PLG006/PLG008 +
  manifest filtering) and a 23-check wire suite (inactive → nothing exists; activate; the whole
  lifecycle; sibling tenant still sees nothing), full regression matrix green, UI screenshot of
  the host grid carrying the plugin's button, column and filter.
- **docs/31 phase 2 — slots + event contracts**: the host declares a contribution point ONCE
  (`model.Slot("web.orders.detail", slot => slot.Key("orderId"))`) and every active plugin's
  panel lands there unnamed — `plugin.Panel(slotId, grid, bind)` binds the plugin's own grid's
  query fields to the slot's record context (PLG007: slot exists, grid is the plugin's own,
  binds name real fields/keys; plugins cannot declare slots). Manifest gains `slots`
  (activation-filtered); tam-react ships `<PluginSlot id context actAs>` and the erp order
  modal hosts it — the invoicing panel appears with zero host knowledge of the plugin. Events
  became CONTRACTS (D-X5): `PublishesEvent(type, fields)` declares payload shape;
  `plugin.RequiresEvent` + every `OnEffect`/event trigger target must name a declared event
  (PLG009) — a typo'd subscription is now a build error, not a silent no-op; manifest `events`
  carries fields + `subscribedBy` (the GatedBy symmetry). Known wrinkle recorded in docs/31:
  packaged fields render user-editable in host edit forms (extension channel) — a `readOnly`
  packaged-spec flag is the queued fix. Verified: 127 unit tests (PLG007/PLG009 + manifest
  filtering + slot-declaration guard), invoicing wire suite grown to 26 checks (slot panel with
  bind, event contract with subscribers, inactive tenant sees an EMPTY slot), regression matrix
  green, UI screenshot of the invoice panel inside the order edit modal.
- **Framework-composed pages** (docs/32 D-P1..3) **+ readOnly packaged fields**: a PAGE is now
  a declared model composition — `model.Page("orders", …)` = grid + record surface (detail view
  by key, edit form prefilled from same-named detail fields, title field, slots) — rendered by
  tam-react's `<ModelPage>`: row click → detail fetch (per-row act-as for subtree rows) → modal
  with form + plugin panels. PAGE001 verifies every part; nav `{page}` targets resolve
  registered React pages first, then declared pages, and declared pages DERIVE nav visibility
  from their grid (NAV005 relaxed). The sample's hand-written OrdersPage React component is
  DELETED — the app's registerPage count is zero, the tripwire metric at its floor. The
  docs/31 wrinkle is closed: `ExtensionField(..., readOnly: true)` marks plugin-owned state —
  grids/filters yes, forms no, wire extension writes rejected (`extensions.read-only-field`) —
  only the owning plugin's writer sets it; invoicing's invoiceStatus uses it. Verified: 131
  unit tests (PAGE001 gates, NAV005 both directions, readOnly spec→manifest), nav wire suite
  asserts the declared page shape + derived permission, invoicing suite asserts readOnly on
  the wire, full matrix green, UI screenshot of the framework-rendered record modal (form
  without the read-only field, slot panel below, title from the declared field).
- **Pages v1.1 — ordered sections + SLOT001** (docs/32 D-P4/D-P5): a page is now an ORDERED
  list of sections (any number of grids and page-level slots; the FIRST grid opens the record)
  and the record surface is an ordered form/slot section list — declaration order IS layout
  order, so "position hints" are structure, not annotations (a slot declared before the form
  renders above it). Page-level slots render their panels unbound (no record context).
  SLOT001 closes the orphaned-slot hole: a declared slot referenced by no page is a BUILD
  error (panels would never render — the nav "more" lesson applied to slots); slots placed by
  custom React declare external: true. Manifest pages section is now {sections:[{kind,id}],
  record:{..., sections}}; ModelPage renders both levels in order. Verified: 133 unit tests
  (ordered manifest shape incl. slot-above-form, multi-grid + page-level slot composition,
  PrimaryGridId, SLOT001 both ways), nav wire suite asserts the sectioned shape, full matrix
  green.
- **Quality pass: convention defaults, splits, tutorial catch-up**: Form/Grid configure is
  now OPTIONAL (docs/32 D-P6 — the record IS the form; result fields ARE the columns minus
  id/version plumbing; configure only to deviate), applied where enumeration was pure
  boilerplate with a manifest diff proving equivalence (two deliberate additive fixes fell
  out: audit gains entityId, checklist create gains its orderId reference). Core splits per
  docs/29: Plugins.cs → Plugins/PluginHandlers/PluginBuilder; TamModelBuilder.Seams.cs
  partial. Tutorial concept-coverage audit (agent-run gap table over docs/21-32): NEW Step 18
  "The composed UI" (nav tree, declared pages, slots, subtree grid, the shell-in-entirety) +
  patches for automation rules (Step 9), vault/outbound (Step 10), framework+tenant packages
  (Step 13), subscription anchors (Step 14), no-switch create (Step 15), event contracts +
  readOnly (Step 17), and five stale sentences fixed (retired overview, free-plan wording,
  modal references). The follow-up consistency pass landed too: steps 3-5 rewritten from the
  design-era attribute-style bindings onto the built TamModelBuilder fluent surface (Form/Grid
  in the composition root, Px lambdas on the binding, ViewCapabilitiesBuilder with nameof).
- **Nav v2 — the tenant override registry** (docs/30 D-N5, the last unbuilt piece of the nav
  design): the `tam.nav` framework package (the twelfth) ships `nav.override` — upsert one
  node's override: hidden, per-culture labels, order, parent, the CLOSED mutation set,
  validated against the COMPILED tree so overriding an inactive plugin's node stays legal —
  and `nav.retire` (delete the row = restore the declared default; unlike extension fields
  there is no data behind it to preserve), plus the `nav.overrides` view + `web.nav` admin
  grid riding the D-P6 defaults. `NavOverlay.Apply` transforms the manifest at the route:
  hide prunes the subtree, order replaces, parent moves a PAGE under a section resolved
  against the post-prune tree (a move into a hidden or absent section is DORMANT, not broken
  — reactivate the plugin and the tenant's placement returns intact), labels merge into the
  per-culture catalogs; the override fingerprint joins the overlay revision so the ETag moves
  with every change. Nav stays discoverability, never authorization (D-N6): hiding removes
  the menu entry, not the surface. Deferred lean: `nav.define-section` waits on real demand.
  Verified: 7 overlay unit tests (hide/order/move/labels, both dormancy directions,
  fingerprint stability), 14-check wire suite (validation findings, ETag movement, per-tenant
  isolation via act-as, admin grid rows, retire restores the baseline tree byte-for-byte),
  full matrix green on a fresh DB.
- **Customers is the SECOND declared page (docs/32 generalizes)**: grid + record surface
  (customers.detail → web.customers.edit) declared in the composition root — no React, no
  slots (the customers surface becomes a contribution point only when a plugin needs it). The
  slice added the missing record pieces the orders page already had: a `customers.detail` view
  (inherited scope, sensitive-gated contact fields) and a `customers.edit-contact` operation
  (Change<T> three-way partial edit; new `customers.edit` atom granted to dispatcher). Nav
  target flipped from { grid } to { page }; permission still derives. Verified: nav wire suite
  asserts the declared shape (10 checks now); a wire probe edits phone via Change<T> and
  re-reads the detail; full matrix green; manifest additive; registerPage count still ZERO.
- **Field-service arc M2 (docs/34): WorkOrder — the real state machine, still zero framework
  changes**: Draft → Scheduled → InProgress → Done → Closed as entity methods behind 7
  intent operations (EDIT001 all the way — even scheduling is an intent that assigns AND
  dates); start/complete own-scoped with -all pairs, so Tekla runs her own orders end to
  end while Didrik works the board (both boundaries wire-proven incl. 403s); editing locks
  once work starts; completion publishes work-order-completed (the M4 invoicing seam,
  effect verified on the envelope). Assignees resolve against the framework's membership
  table with the display name SNAPSHOT onto the entity (no cross-provider actor join —
  friction logged), and the schedule form's options ride a ServerDerivation through the
  resolve endpoint. The runtime custom field story generalized to the new entity on the
  first try (boolean requiresLift, seeded like an admin would, riding list rows).
  Verified: 18-check fieldm2 suite + the full 13-suite matrix on SQLite AND Postgres (RLS
  on WorkOrders confirmed). Friction log: +3 entries (actor-reference rendering; the
  missing declarative lookup renderer — now the arc's clearest framework ask; positive
  cost-of-a-state-machine datapoint).
- **Field-service arc M1 (docs/34): Project deepened + StockItem — the consumer baseline**:
  the stress-test arc's first slice, built with ZERO framework changes and zero React.
  Project gained number (unique per tenant), status, budget, and close/reopen INTENT
  operations (EDIT001) with a cross-aggregate guard (open orders block closing); StockItem
  is the new per-node catalog (sku, unit, price, retire-don't-delete). 7 operations, 4
  views (projects.list is subtree-capable with the mechanical company column), 2 more
  declared pages — registerPage count still ZERO; the stock page needed no form/grid
  configure at all (the record IS the form). Verified: new 21-check wire suite (fieldm1:
  duplicate-number/SKU findings field-targeted, Change<T> edits with {original,value},
  close guards, role boundaries didrik/tekla) + the full 12-suite matrix on SQLite AND
  Postgres with RLS policies confirmed on both new tables. Three friction-log entries
  recorded in docs/34 (money-by-name-sniffing confirmed live; flat label namespace
  collision labels.number; Change<T> raw-wire discoverability).
- **The docs SITE (GitHub Pages) — LIVE at https://nilbin.github.io/abc/**: MkDocs Material
  over docs/, rebuilt and republished by .github/workflows/docs.yml on every push to main.
  Deployment is `mkdocs gh-deploy` onto the gh-pages branch — the Actions token can push a
  branch but can never CREATE a Pages site through the API (that needs repo-admin rights),
  which is why the configure-pages/artifact flow kept skipping. The
  tutorial is split one page per step (docs/tutorial/step-00..18 + tally; docs/20-tutorial.md
  stays as a pointer so inbound links survive), docs/index.md is the landing page, THIS file
  publishes as /status (the progress page), and docs/llms.txt is the machine-readable index
  (llms.txt convention: every page with a one-line description + raw-markdown pointers).
  Site nav groups the design docs by theme (Foundations / Extensibility / Tenancy & security /
  UI composition / Process). Built with --strict (broken links fail the build).
- **Plugin-declared pages (review round 4 finding 3, built)**: a plugin's own aggregate now
  gets a full record page without host React — `plugin.Page("invoicing.invoices", …)` under
  the plugin prefix (PLG001), activation-filtered in the manifest, placed through the same
  nav suggestion machinery as everything else (existence is the declarer's, placement stays
  the host's/tenant's). A record with NO form renders the detail view's fields read-only in
  the modal — the invoicing showcase: grid + read-only record (status moves through
  finalize/mark-paid operations, never an edit form), nav target flipped from { grid } to
  { page }. Verified: 149 tests (tagging, activation filtering both ways, PLG001 squatter
  rejection), invoicing wire suite asserts the declared page shape (27 checks), full matrix
  green, manifest additive.
- **The authoring reshape (review round 4's recommendation, built)**: behavior registration
  now lives ON the behavior — [Gate("orders.complete")]/[GateAll]/[OnEffect("event")]
  attributes on IOperationGate/IEffectHandler classes, discovered by the source generator
  exactly like [Operation]/[View] (AddGateType/AddSubscriberType are the add-by-type
  substrate; PLG012 rejects attributed-but-wrong-shape types; plugin scope enforced). Big
  plugins compose Configure from explicit IPluginParts (plugin.AddPart<OrdersContract>()) —
  invoicing is the showcase: Configure is four lines, the host-facing contract and the UI
  surface are cohesive parts, behaviors sit in Features.cs. PluginBuilder is now the ONE
  receiver (Form/Grid/PublishesEvent/Add*Type forwarded) — the plugin.Model flip-flopping is
  gone from every package and sample. Proof: the manifest is BYTE-IDENTICAL before/after
  (gates, subscribedBy, events all registered by the new path), 148 unit tests, full wire
  matrix green (every attribute-registered behavior exercised in anger). docs/22/29 and
  tutorial Step 13 updated to the new shape.
- **Review round 4 — end-to-end triage with two docs-only implementers**: three review agents
  (architecture + authoring shape, tutorial/DX fidelity, adversarial correctness on the newest
  surfaces) plus two "RTFM" agents who each built a working plugin (timesheets: the full
  cross-domain surface; assets: the basic path) from the DOCS ALONE — framework source
  forbidden, compiler errors as IntelliSense. Both shipped everything; both said "with-fixes";
  both independently hit the same walls, and those walls got fixed:
  TAM007 (ctor projections in views — built green, 500'd on first sorted request; now a build
  error, zero violations in-tree); nav suggestions collect into a matching section OR mode
  (both plugins suggested "work" and silently landed in "more"); L10N001 now gates operation
  OUTPUT labels (18 missing keys found and added) and lists every missing key at once.
  Correctness fixes: PLG005 host-only guards (a plugin could reach AddPlugin and ESCAPE
  namespace enforcement entirely); PackagedFieldWriter re-checks the tenant boundary after
  FindAsync (EF Find bypasses global filters — the app must not lean on RLS, D-R1);
  SubtreeRead widening made execution-local (a widened read no longer leaks into the write
  path's scope); RLS interceptor fingerprint advances only after set_config succeeds + the
  tag scan survives EF's blank-line tag rendering; PAGE001 requires the record form to carry
  the key input; the stamp interceptor refuses blank-TenantId inserts in escalated scopes.
  Architecture: manifest memoized by ETag (cold requests stop paying the reflective rebuild);
  hot-path reflection memoized (MethodInvoker + accessor caches); NavOverlay extracted to
  pipeline infrastructure (docs/29 litmus); fortnox rewired onto IHostViewReader +
  RequiresView (the sample taught the reach-around D9 kills); inbox drain bounded server-side
  (ReceivedAtIso). Docs: the tutorial REWRITTEN onto the built APIs — new Step 0 (host from
  nothing, incl. MapTam which was never mentioned), the locale-key grammar box, real handler/
  wire/client/MCP shapes throughout Steps 1-12, honest DESIGNED-NOT-BUILT markers (Step 11
  harness, impact reports, L10N000/002/003), RLS in Step 15; docs/21 aligned to the real flat
  key grammar; docs/22 gains the plugin authoring reference the RTFM run proved necessary.
  Deferred with intent: the authoring reshape ([Gate]/[OnEffect] attributes + plugin parts —
  designed, next milestone), plugin-declared pages, wildcard-gate set caching, packaged-writer
  unification onto the operation path, RLS read-set scaling for deep trees.
- **The RLS backstop (docs/19 D2 → docs/33, D-R1..R8)**: PostgreSQL row-level security now
  mirrors the EF tenant filter — `TamRls.ProvisionAsync` walks the EF model and puts
  ENABLE+FORCE RLS plus one FOR ALL policy (current tenant ∨ subtree read set ∨ the explicit
  cross-tenant sentinel) on every ITenantScoped table, refusing to run as a role that would
  silently bypass RLS; `TamRlsInterceptor` (one class, three EF interceptor roles) keeps
  `app.tenant_id`/`app.tenant_read_set` true to the ambient scope per command, re-syncing
  after pool resets and transaction rollbacks (set_config is transactional). The first
  Postgres run was the design input: sanctioned cross-tenant reads fail closed unless the
  database can SEE the sanction — so `AcrossTenants()` (IgnoreQueryFilters + a query tag the
  interceptor honors only in EF's leading comment block) replaces raw opt-outs at every
  framework site, the auth branch escalates its requests explicitly
  (`TenantScope.EscalateCrossTenant`, `/connect/*`), and the tenants REGISTRY is exempt as
  bootstrap topology. The analyzer accepts AcrossTenants everywhere it accepted
  IgnoreQueryFilters. SQLite dev path untouched. Verified: 143 unit tests; the FULL wire
  matrix green on PostgreSQL with policies active AND again on SQLite; a psql probe as the
  demoted app role proves the backstop directly — no setting → 0 rows (the forgotten-filter
  case), demo scope → demo rows only, read set widens, sentinel spans, cross-tenant
  INSERT/UPDATE rejected by policy; 26 tables covered, registry exempt.
- **Source layout is now a stated convention** (CLAUDE.md + docs/29-code-structure.md): one
  package = one file under `src/Tam.AspNetCore/Packages/` with the package class, findings,
  gates, operations and views co-resident; pipeline infrastructure extracted to named root
  files (PluginActivation, Entitlements, SecretVault) with a litmus test ("would it exist
  without the admin surface?"); Integrations.cs renamed InboundIntegrations.cs. Provably
  mechanical move: manifest byte-identical, 102 tests green, wire smoke passed. The remaining
  organization debts are a checked ledger in docs/29.
- **Rules run through the gate seam they sell**: the executor's hard call to `RuleEvaluator`
  is gone — tenant automation rules (P5) execute as `tam.rules`' own PURE wildcard gate.
  Gates gained a declared `pure` flag: pure-over-input gates run BEFORE the transaction (the
  cheap fail, where rules always ran), transactional gates keep running inside it, and
  non-blocking findings a passing gate returns (a rule's evaluation-failed warning) are carried
  into the response. Wire-verified: define rule → blocks pre-transaction with the
  tenant-authored message in culture → below-threshold passes → retire → stops firing; and the
  layering test fell out for free (tenant rule retired ⇒ the approvals plugin's wildcard gate
  took over the same operation).
- **The framework package tier exists — and the system module IS it** (docs/22): `[TamPackage(id,
  prefixes)]` + `AddPackage<T>()` register through the exact plugin surface (PLG005 seams
  unlocked, contributions tagged), validated against CLAIMED wire prefixes (PLG001 package
  variant — `users.invite` stays `users.invite`, D4-permanent) and ALWAYS active: activation
  consumers union `model.Packages` in, `plugins.activate` rejects package ids (nothing to
  toggle), and the manifest includes package contributions for a tenant with zero activation
  rows. `AddTamSystem()` is now eleven packages — tam.extensions, tam.roles, tam.audit,
  tam.plugins, tam.tenantpackages, tam.rules, tam.tenancy, tam.users, tam.subscriptions,
  tam.vault, tam.integrations — each shipping its own forms/grids, deleting ~130 lines of
  hand-wired admin UI from the sample host (the app no longer names any framework admin
  surface). Core stays core: authorization, tenant isolation, pipeline atoms, entitlement
  enforcement, the activation machinery. 102 tests; full wire matrix green (paired-atom 13/13,
  tenants 11/11, invite, approvals 23/23, inspect 7/7, token hardening).
- **Plugin authoring surface v2 — ctor-DI classes, no service locators**: gates, effect
  handlers and parked work are CLASSES constructed per invocation by `ITamActivator` (cached
  `ActivatorUtilities` factories) — `plugin.Gate<ChecklistGate>("orders.complete")`,
  `plugin.GateAll<ApprovalsGate>()`, `plugin.OnEffect<NotifyApprovers>("approvals.requested")`,
  `gate.Park<ParkEnvelope, ApprovalRequest>(envelope)`. Dependencies are ctor parameters exactly
  like operation-handler parameters; every `((ITamDb)services.GetService(typeof(ITamDb))!).Db`
  cast in the samples is gone, and `GateContext` no longer carries an `IServiceProvider` at all.
  The disposed-scope footgun died structurally: parked work is constructed IN the fresh
  post-rollback scope, so it cannot capture the rolled-back gate scope. Rounded out with the
  review absorptions: `LocaleCatalogs.Localize` (kills the duplicated lookup+format helper in
  UserModule and plugins), `db.Publish(eventType, payload)` (framework-owned outbox row
  conventions, reused by the executor itself), convention-based `plugin.LocaleDefaults()`
  (embedded locales/*.json — the 8-line resource loop deleted from all plugins),
  `EnvelopeReplay.Envelope` record (no adjacent-string transposition; initiator is the actor-id
  string), and `ITamDirectory` (the sanctioned people seam: actor↔email over identity tables —
  approvals no longer queries `AccountEntity` internals). Wire-verified: approvals 23/23 and a
  new inspect matrix 7/7 (class gate blocks/unblocks, class handler opens the follow-up
  checklist), paired-atom 13/13, tenants 11/11, reserved-twin, invite.
- **The approvals package exists** (`samples/approvals`, tutorial Step 16 BUILT): nested
  `ApprovalGroup`s (subgroup members approve for ancestors — plugin semantics, framework-blind),
  `ApprovalRule` rows over host wire ids with optional thresholds (`orders.create` ≥ 100 000),
  the parked `ApprovalRequest` keyed by payload hash, approve/reject with four-eyes +
  through-nesting membership checks, approver notification via the outbox + `ITamEmail`, and
  post-commit replay-as-initiator. Wire-verified 23/23 on a fresh seed: block+park+rollback,
  nested-group release, dual-attributed audit (initiator/Workflow/correlation on the replayed
  op; releaser on the approve op), reject-never-executes, identical-resubmit dedupe, no
  re-decision. The host domain (`CreateOrder`) was not touched. Also fixed en route: the outbox
  dispatcher now pins the ambient tenant per record, so effect subscribers' tenant-filtered
  queries (idempotency checks, envelope lookups) actually see rows.
- **Extension-channel targeting is deterministic and fail-closed** (the old review medium): the
  executor no longer binds `extensions` changes to whichever tracked instance of the extensible
  type came FIRST — one tracked instance is the target; among several, the single Added/Modified
  one is; anything else returns `pipeline.ambiguous-extension-target` instead of guessing (a wrong
  guess is silent cross-record corruption). Wire-verified: create-with-extension lands on the right
  row, unknown keys still rejected.
- **One background-loop shape**: the five drivers (outbox dispatcher, integration retry queue,
  scheduler, retention janitor, token janitor) now share `TamBackgroundLoop` (tick, swallow
  transient failures, wait, repeat — a bad tick never kills a loop) and the three competing
  consumers share `ClaimLease.TryCommitAsync` (roll the lease forward, commit under the row's
  concurrency token, detach-and-skip when another instance won). Verified on the wire: an
  order-completed event was claimed, dispatched and its lease released through the shared path.
- **No-BFF token hardening (docs/26 — settled: no BFF)**: the SPA keeps holding its own tokens
  (sessionStorage, tab-scoped); the server enforces the guarantees. Refresh tokens rotate per use;
  a redeemed token replayed after the 30s leeway is rejected and `RefreshReuseGuard` revokes the
  WHOLE family (shared authorization + all descended tokens) — OpenIddict alone rejects the replay
  but leaves rotated siblings alive; replayed authorization codes get the same cut. signOut()
  revokes the refresh token at /connect/revocation (fire-and-forget). Token + authorization ENTRY
  validation is on for API calls, so revocation bites immediately instead of at access-token
  expiry; a TokenJanitor prunes the store past the longest lifetime. Wire-verified (10 checks):
  rotation, within-leeway retry tolerated, post-leeway replay rejected, rotated sibling dead after
  the cut, public-client revocation accepted, revoked refresh dead, revoked ACCESS token rejected
  by the API immediately.
- **Invite flow (docs/26)**: `users.invite` creates account + membership up front (same
  role/policy validation and seat gate + lease as users.define, via shared MembershipRules) and
  mails a one-shot invite link through the new `ITamEmail` seam (default: `LogTamEmail`, the dev
  inbox); the token is stored only as its SHA-256, expires in 7 days, and is redeemed on a
  framework-rendered `/connect/invite` page that sets the password. Inviting an account that
  already has a password just adds the membership and mails a notification (`inviteSent: false`).
  Wire-verified (12 checks): invite → link from the log inbox → login refused before accept →
  weak password re-prompted → accept → token one-shot → login works → listed active with the role
  live → existing-account path without token → bad token rejected.
- **Review round on the new surfaces** (two adversarial agents over Axis 2 + lifecycle + UI), all
  findings fixed and wire-verified: (1) HIGH — policy scope values were validated case-insensitively
  but ENFORCED ordinal, so `{"orders":"Own"}` validated and then silently failed OPEN (full access);
  scopes are now canonicalized (lower-cased) at define time and the fail-open is proven closed on
  the wire. (2) MED — `TenantEntity` is now `IVersioned` and structural ops take a "structural
  lease" (create/move bump the attachment parent's version), so a concurrent move/create pair that
  would bake a stale parent path into a child fails at SaveChanges instead of silently orphaning a
  subtree. (3) `tenants.create` with the ACTIVE node's own id on a pre-hierarchy tenant now returns
  `tenants.duplicate-id` instead of a 500 (double-Add of the self-healed root). (4) `tenants.rename`
  added (subtree-guarded; the only way to give a self-healed root a real display name) with form +
  toolbar action. (5) ScopeMap renderer: toggling all|own no longer reorders rows; adding an
  already-listed resource is disabled instead of silently resetting it to `own`. Policies never
  narrow a `*` role grant — documented as deliberate in docs/27 and at the narrowing site.
- **Access policies (docs/27 Axis 2 v1) — BUILT, wire-verified, then RETIRED** by the ownership
  surgery (docs/28): policy-authored `own` structurally could not be made fail-closed (whether a
  grant carried the scope was runtime data no analyzer could tie to per-view discipline). The
  registry, operations, membership field, admin page and the whole `:own` suffix machinery were
  removed; the paired-atom pattern above replaces them with the same runtime admin power and a
  compile-time guarantee. Kept lesson: prefer encodings an analyzer can verify.
- **Postgres parity for the policy + lifecycle stack**: all four new suites re-run on PostgreSQL 16
  (fresh DB) with zero code changes — access policies (11/11), tenant lifecycle (11/11), scope
  canonicalization fail-closed, rename + guards. The `tenants.Version` concurrency column is live
  and the structural leases visibly bump attachment parents on create/move/rename.
- **Postgres parity for the hierarchy/capability stack**: the full suite re-verified on PostgreSQL 16
  (fresh DB, `Host=` connection string, LISTEN/NOTIFY backplane registered): cascade login at a
  descendant, subtree roll-up (tenants-table semi-join / LIKE), inherited customers (ancestor
  IN-list), act-as create with per-node numbering + coherent side-artifacts, read masking, level
  role definition, and act-as denial — all identical to SQLite. No code changes required.
- **Subscriptions & seats (docs/24)**: subscription registry (plan, seats, plugin entitlements,
  status) driven by subscriptions.set-plan (service-actor only) with a subscriptions.current
  view; plugins.activate is gated by plan entitlement and users.define by the seat ceiling —
  both localized upsell findings, both degrading to a free-plan default when no row exists.
  The seat gate now covers REACTIVATION too (users.define on a deactivated membership consumes a
  seat like a new one — gating only brand-new rows let deactivate/reactivate churn breach the
  ceiling; found and fixed on the wire), and consuming a seat takes a SEAT LEASE — the subscription
  row is IVersioned and gets written (or materialized, for the free default) in the same
  transaction, so two defines racing past the count check conflict at SaveChanges instead of both
  slipping under the ceiling.
  Verified: entitled activation succeeds, free plan blocks it, seats=5 blocks the 6th user,
  raising seats admits it, non-admin cannot set-plan (403).

- **Plugin-shipped integration (docs/10 + docs/22)**: the Fortnox order import moved out of the
  sample into its own `fortnox` plugin — proving a plugin ships integrations, not just fields
  and gates. `PluginBuilder.Integration(id, operationId, key, map)`: the framework maps
  `POST /api/integrations/fortnox.orders.import`, gated by activation + entitlement; each array
  element is stored in the inbox under its document number and mapped to `orders.create` wire
  input, re-run per retry. The mapper resolves the vendor customer name through the host's
  `customers.lookup` VIEW as the actor — never a host table, no host CLR types. Verified: 404
  while inactive, a two-row batch (one known customer created, one unknown failed), then
  creating the missing customer recovers the failed row from the inbox with no re-send, and
  reposting is idempotent (no duplicate orders).

- **Secrets vault + external integrations (docs/25)**: per-tenant settings (clear) and secrets
  (encrypted via ASP.NET Data Protection — no dependency, key ring swappable for Azure KV/AWS
  KMS in prod). `secrets.set` is WRITE-ONLY: `secrets.list` shows keys + a set flag, never the
  value; verified the DB holds 155 bytes of ciphertext, not the plaintext, and no view/manifest
  surface leaks it; `secrets.manage` gates it (non-admin 403).
- **Outbound integrations (docs/25)**: plugins ship `OutboundIntegration(id, trigger, handler)`
  where the handler reads settings/secrets and does HTTP against an external system. Three
  triggers, all activation-gated and recorded to `integration_runs`: EVENT (the outbox fires
  it post-commit — verified: completing an order pushed `{"orderNumber":"2026-01416"}` to the
  mock accounting API and logged an event/ok run), SCHEDULE (a one-minute `IntegrationScheduler`
  hosted service + `integrations.schedule`, spec `every:Nm`/`daily:HH:MM` — verified next-run,
  invalid-spec rejection, and that an event integration can't be scheduled), and MANUAL
  (`integrations.run`). The Fortnox plugin is now a two-way connector (inbound import + outbound
  push + scheduled poll), none of it host code.
- **"Does rolling our own bite?" — review-round-2 hardening (docs/25)**: review agents on
  code/scalability/novelty found the bites in the hand-built vault/scheduler/runner; each is closed
  and verified on the wire. **SSRF egress guard** — the outbound client resolves the host itself and
  connects only to a validated public IP (blocks loopback/link-local/private/CGNAT v4+v6, closes the
  rebinding window) and never follows redirects; secure by default, `AllowPrivateNetwork` opts in
  (the demo's localhost mock). **Reserved permissions** — `"*"` no longer confers
  `subscriptions.manage`, so a tenant admin (or a plugin as the system actor) can't self-entitle;
  verified a `"*"` admin now gets 403 on `subscriptions.set-plan` while `subscriptions.read` still
  200s. **Multi-node scheduler lease** — `NextRunIso` is an optimistic-concurrency token, claimed
  before the run, so instances don't double-fire; `(Enabled, NextRunIso)` index replaces the
  full-scan; per-run timeout stops a hung handler wedging the tick. **Fail-closed** — malformed
  inbound JSON is 422 (verified), incomplete rows become validation findings (verified, no 500),
  overflowing schedule specs return invalid instead of throwing. **Per-tenant secret binding** and a
  request-scoped **`ActivationCache`** (one query for the 3–4 per-request activation reads). 17 new
  tests (64 total); manifest baseline still additive-only.

- **Durable messaging — review-round-3 (docs/25)**: the outbound side and the outbox now share the
  inbox's proven retry primitive. A shared `RetryPolicy` (exponential backoff, dead-letter cap)
  drives a new `outbound_tasks` queue + `IntegrationRetryDriver`: a failed event/schedule push
  retries with backoff and dead-letters after the cap — verified end to end (broken API key →
  `event failed` → `retry failed` → `retry failed` → dead-letter, then fix-secret + `integrations.requeue`
  → `retry ok`). The **outbox** got a claim-lease (optimistic-concurrency token, so N instances stop
  double-delivering webhooks/emails) and a poison dead-letter (one bad payload no longer wedges the
  stream); event pushes are per-run time-boxed; activations are looked up once per tenant per tick.
  A unified `integrations.dead-letter` view + `integrations.requeue` op, a retention janitor (trims
  dispatched outbox / processed inbox+tasks / old runs / expired idempotency past 30d; audit and
  dead-letters kept), and a manifest `ETag`/`304` (verified) round it out. 74 tests; baseline additive.

- **Security round 3 (review follow-ups)**: closed the ways the round-2 `Reserved` fix could be
  side-stepped and tightened auth. `roles.define` and package install now **reject reserved
  permissions** (`subscriptions.manage`) — verified a `"*"` admin gets `roles.reserved-permission`
  instead of being able to mint a role that carries it and self-entitle (a normal role define still
  200s; `subscriptions.set-plan` still 403). **Cross-tenant access is membership-bound**: since the
  identity/PKCE work (docs/26) the token's `tam:tenant` claim *selects* the active tenant, and
  `ClaimsActorProvider` grants only what the account's membership in that tenant carries — an account
  with no membership there gets no grants, so a token can't act in a tenant the account doesn't belong
  to (superseding the earlier same-name token-equality check).
  **Idempotency is actor-scoped** — verified two actors reusing the same key + payload get independent
  outcomes (no cross-actor replay), while same-actor replay still returns the stored result. The
  refresh-token grant (advertised but never redeemable) was **dropped** (now `unsupported_grant_type`),
  and the SSRF egress guard gained `192.0.0.0/24`, `198.18.0.0/15` and limited-broadcast. 78 tests.

- **Tenant isolation as a model property (not 50 Where-clauses)**: row-level tenant scoping was
  hand-written at ~50 call sites and *omitted at 11* domain queries in the sample (latent
  cross-tenant leaks the moment a real tenant provider is wired). It's now one EF global query
  filter over every `ITenantScoped` entity, keyed off an ambient `TenantScope` set per request by
  middleware; background jobs (scheduler/outbox/retry/retention) and the vault's explicit-tenant
  reads opt out with `IgnoreQueryFilters`, and the startup seed guard does too. Proven by a test
  with **two context instances carrying different tenants over one cached model** — each sees only
  its own rows (guards against the model-cache capture pitfall); a cross-tenant by-id read returns
  nothing with no manual filter. Verified on the wire: single-tenant views still return data, auth
  resolves, the background outbound push still fires, and the seed stays idempotent across restart.
  81 tests.

Screenshots of all of it: [docs/screenshots/](docs/screenshots/).

## Gaps vs. the design docs (deliberate, in rough priority order)

1. **Compiler package**: analyzer (TAM001-003, L10N001, EDIT001 as build errors) + incremental
   source generator emitting compile-time discovery (`AddDiscovered()`, visible under
   samples/erp/generated/ — no runtime assembly scanning). Field metadata is still reflected at
   startup; L10N000, DB001, EDIT002 and impact reports remain.
2. **View result records are init-property, not positional** — EF cannot compose sort over
   positional-record ctor projections; the compiler phase should rewrite sort into the projection
   source so the tutorial's positional style works.
3. **Context views in forms** (`form.Context/Show`) not implemented; contextual display data flows
   via derivations/suggestions instead.
4. **Value update policies**: only `RecomputeIfUntouched`; `DefaultOnce/Derived/RequireConfirmation`
   and `SuggestFrom` bindings are absent. Conditional requiredness is enforced at resolve +
   client, not re-checked at submit.
5. **Authorization**: identity is still the X-Demo-Role header stand-in (no real authn), and
   only the Own scope exists — Team would need an org dimension.
6. **Tenancy**: envelope + stamping + per-tenant registry/overlay work, but a fixed "demo" tenant,
   no EF global filters, no RLS (D2).
7. **Idempotency**: replay + payload-hash rejection work; a retention policy doesn't exist yet.
8. **Integrations**: mapping binding (INT001 validation), idempotent runner, and a persisted
   inbox with retry + dead-letter (3 attempts) exist — failed-sync recovery verified: a row that
   failed on a missing customer processed automatically after the customer was created, with no
   re-send. Outbox implemented: explicit event effects persist in the operation transaction and a
   background dispatcher delivers them (SSE transport in the demo; IOutboxTransport for a real
   bus). Reconciliation remains. Also not started: offline/mobile.
9. **MCP**: minimal JSON-RPC over HTTP (no resources, no streaming). Tool schemas are now
   per-tenant and include extension fields with admin-authored descriptions.
10. **PostgreSQL supported and CI-smoked**: connection-string switch (Host=… → Npgsql), real
    `jsonb` extensions column, full wire regression verified on PG 16. SQLite remains the
    zero-setup dev default. Extension filtering is now TYPED JSON extraction (see the verified
    list): exact equality + contains + ordinal ranges for strings/dates, numeric equality +
    ranges for numbers, on both providers. Extension SORTING now works the same
    way (`sort=ext.weightKg` — numeric via JsonNumber, ordinal via JsonValue; grid headers on
    extension columns are clickable; null placement follows the provider). Boolean extension filters now work too
    (provider-aware: json_extract's 1/0 on SQLite, ->> text on PostgreSQL), and SQLite JSONPaths
    quote the key so plugin-packaged dotted keys ("inspect.requiresInspection") resolve. The
    remaining performance item is expression-index promotion.
11. Grid row-action input mapping is a name-match heuristic; batched per-row action availability
    (review-notes risk #4) not implemented.
12. **Plugin system: P1–P3 and P5-v1 built and verified**; remaining design-only
    ([docs/22-plugins.md](docs/22-plugins.md), decision D8, tutorial step 13): P4 custom
    objects (waits on typed JSON predicates + index promotion + RLS), rule actions beyond the
    blocking finding (set-field, publish-event), effect-triggered rules, a rule-builder UI,
    and rules inside tenant packages. PLG###/RUL### are runtime errors/findings today, not
    Roslyn analyzer diagnostics; subscriber delivery is at-most-once (no retry/inbox for
    plugin subscribers yet); field-level audit shows the extensions column as one change, not
    per extension key; package uninstall leaves package-defined roles in place (they may be
    granted to real users); client-side portable evaluation of tenant rules (offline parity)
    needs the rule conditions merged into the manifest, which is not done yet. Packages and
    rules now have admin UI pages (grids + forms; the rule condition is authored as raw Px
    JSON in a textarea — a visual rule builder remains future work).

## The one-night verdict

Everything architecturally risky that could be tested in one vertical slice — portable AST with
dual evaluators, manifest-driven rendering, three-way merge semantics, the extension overlay, the
localization gate, MCP-from-model — worked as designed. The costliest remaining item is the real
compiler package (source generator + analyzers + impact reports), which is exactly what the
design predicted (docs/review-notes.md).
