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
                             system, and the framework admin surface as FIFTEEN always-active
                             [TamPackage] modules (users/roles/audit/tenancy/rules/nav/documents/
                             developer/reach/... with their forms, grids and embedded sv/en locales)
src/Tam.AspNetCore.Postgres  the Postgres LISTEN/NOTIFY SSE backplane + the RLS backstop
                             (TamRls: policies over every tenant-scoped table, scope-synced
                             session settings — docs/33)
src/Tam.Auth.OpenIddict      embedded OpenIddict token server + ClaimsActorProvider (the
                             framework's own auth, behind the IActorProvider seam)
src/Tam.Testing              in-process pipeline test harness: TamTestHost, envelope
                             assertions, CapabilitySweep (tutorial Step 11)
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
tests/Tam.Tests              191 tests: merge, extension applier, Change<T> JSON, portable AST,
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
- **Authorization (D1, first layer)**: role-based actors, pipeline 403s with localized findings,
  actor permissions in the manifest overlay, and the UI hides ungranted actions (verified: viewer
  sees no create/complete/custom-fields surfaces). NOTE: the `X-Demo-Role` header was the ORIGINAL
  stand-in for identity in this layer; it has since been replaced by real authentication — the
  embedded OpenIddict server (Auth Code + PKCE for humans, client credentials for agents) resolves
  the actor and active-tenant membership (see the Auth section of the capability matrix). D1 is the
  authorization layer that rides on top of whichever `IActorProvider` supplies the actor.
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
- **Beauty arc 1 — the vocabulary & dedup sweep (docs/29 "Conventions")**: the review's
  compounding-debt findings fixed at the root. New authorities, each replacing a per-file
  idiom: `LabelKeys` (every label-key grammar minted in one class — 12 interpolation sites
  replaced), `Naming.IsSlug`/`IsCamelKey` (GeneratedRegex; the 5 shared slug/key checks route
  through them, and rules/extensions gained their OWN teaching findings `rules.invalid-name` /
  `extensions.invalid-key` instead of a bare invalid-value), `ActivationCache
  .ContributionExistsAsync` (the docs/22 existence rule asked in ONE place — 4 executor/endpoint
  re-implementations plus the Outbox's hand-union deleted), and `TamEndpoints.FindingsResult`
  (non-operation endpoints answer findings through one envelope + one code→status mapping —
  views/resolve/integrations hand-built objects gone; UnknownForm now correctly 404s, and
  integrations.malformed-payload became a real localized Finding). 69 redundant `[LabelKey]`
  attributes deleted (equal to the convention default — proven labelKey-neutral by manifest
  diff) plus the type-carried CustomerId/ProjectId/StockItemId repeats across 6 ERP files;
  `labels.next-run-iso` relabeled without the storage suffix. The tenant WORD is now coherent:
  framework catalogs say organization/organisation; samples/erp overrides to Company/Bolag —
  proving the host-brand override layer with the manifest. users.define/invite share
  `EnsureActiveMembershipAsync` (the one-helper-short extraction finished); InstallPackage's
  in-pipeline explicit TenantId stamps removed (ambient rule stated once); the SystemOps
  namespace vestige unified into Tam.AspNetCore; orphaned doc comments, split-file dividers
  and the invoicing dead scaffolding swept. All rules written down in docs/29 "Conventions"
  (wire-id grammar with grandfathered deviations, name shapes, label keys, findings, stamping).
  Verified: suites 162+38, wire 18+22 on fresh SQLite AND Postgres, additive baseline,
  labelKey-diff zero, docs check green.
- **`dotnet tam` — the artifact tooling, productized (docs/38, BUILT)**: the hand-run regen
  dance (and a plugin-slice CI gate that hardcoded `invoicing`) became a packaged .NET tool,
  `Tam.Cli`, so a framework CONSUMER gets the same surface in their own repo — no bespoke
  scripts. `dotnet tam regen` rewrites every committed artifact (manifest baseline, host
  contract, every plugin contract slice, TS client); `dotnet tam verify` re-exports to a temp
  dir and byte-compares (the CI freshness gate); `manifest`/`contract` are one-off pass-throughs.
  Zero hardcoded paths — layout comes from a `tam.json` found by walking up from cwd, and plugin
  slices auto-discover from the committed `*.contract.json` files (so a new dependency parent
  needs no edit anywhere). The three separate CI freshness steps collapsed to one `tam verify`
  (`dotnet run --project src/Tam.Cli -- regen|verify` in-repo); the additive D4 check stays
  separate (freshness vs additivity are different concerns). The exports still can't be a source
  generator — they serialize the composed runtime model — and the slices stay committed build
  inputs (a plugin references them as AdditionalFiles); the tool just removes the friction and
  the tackiness. Verified: `regen` produces a byte-identical tree, `verify` passes clean and
  fails (naming the file, exit 1) on a tampered slice; docs/38 documents the consumer story.
- **Plugin-on-plugin: the `DependsOn` edge, both levels (docs/37 D-V4, BUILT)**: a plugin may
  now declare `DependsOn(otherPlugin)`, and that declared edge is what lifts PLG010 to consume
  the parent's contract. Landed in five verified slices on nilbin/abc main. (1) DependsOn
  declaration + PLG011 (acyclic, registered-target, no self-edge, run before the PLG010 sites)
  + the PLG010 relaxation over the transitive edge closure. (2) L1 activation guards — a plugin
  activates only where its parents are active (`plugins.dependency-inactive`) and cannot be
  deactivated under an active dependent (`plugins.dependent-active`), keeping the invariant true
  continuously; cascade/suspend stays deferred. (3) Per-plugin contract export
  (`contract --plugin <id>` → a slice of only that plugin's surface, stable against host
  changes) + a CI freshness gate; invoicing.contract.json committed. (4) The source generator's
  artifact filter widened to `*.contract.json` and its facade emitter merged across every
  referenced artifact (ids globally namespaced, so no collision). (5) The proving consumer:
  `fortnox` DependsOn `invoicing`, consuming its `invoice-finalized` event through the generated
  `InvoicingInvoiceFinalizedEvent` facade and pushing the finalized invoice via a new outbound
  integration — which also closed the outbound-trigger PLG010 ownership gap. The erp host now
  composes a real cross-plugin edge (a build PLG010 would reject without the declaration).
  Verified: Tam.Tests 196 (declared-edge-accepted on all three seams; cycle / unknown-target /
  self-dependency each PLG011); a new verify/plugin-on-plugin.mjs drives the L1 guards on the
  wire; full matrix 16+18+22+31+7 green on fresh SQLite; manifest/contract/types byte-stable
  (the edge is model-only). `Provides`-one-of and `Conflicts` (mutual exclusion) remain
  designed-not-built, as does the heavy lifecycle machinery.
- **Variability design (docs/37, DESIGNED not built)**: the answer to "how do order surfaces,
  forms and flows differ by country, trade and company size" — written down before any code,
  closing the framework-finish arc. One compiled model; variation routes through three
  existing channels (activation+entitlements, overlays, tenant data). New pieces designed:
  tenant profile facts (`country`/`trades`/`size`, closed vocabulary, `tenants.set-profile`,
  nearest-ancestor inheritance), a `tam.forms` form overlay registry mirroring nav v2
  verbatim (closed mutation set — hide/order/labels/tighten-require/group/validators —
  FRM001–005, dormancy, tenant tier + fact-keyed pack presets), compiled named validator
  sets (tighten-only, manifest-carried), and the conventions trade=plugin / size=plan /
  country=locale+presets+validator-sets. The structured-address superset is the designated
  proving consumer (also the opening move of the parked order-location arc). Went through a
  four-lens review round (YAGNI, prior-art vs Odoo/SAP/Dynamics, framework-coherence against
  the real code, lifecycle/marketplace) that dissolved the rev-2 scaffolding, then a design
  conversation with the user landed REVISION 3: **variability is just plugins at capability
  granularity**. Each capability is its own small plugin (Fortnox, Skatteverket, ROT, RUT,
  electrician, SäkerEl) — country and trade are EMERGENT from the active set, not framework
  concepts and not "axis/bridge" tiers (both deleted). The existing tenant-package tier (P3)
  is the RETROACTIVE onboarding bundle ("Swedish electrician starter", minted once a
  tenant-shape recurs — no pre-built matrix). The one genuinely new framework concept is the
  **plugin relationship model** (D-V4): `DependsOn` at two levels (L1 activation dependency,
  no code coupling, needed now as the guardrail; L2 contract coupling that relaxes PLG010
  along a declared acyclic edge + exports the parent contract as a second provider, when the
  first cross-plugin hook lands), `Provides`-one-of singleton capability slots for mutual
  exclusion (Fortnox vs Visma; one invoice tax-treatment per node), `Conflicts` as escape
  hatch; chains allowed at any depth (acyclicity is the guard, not a depth cap). Profile
  facts demoted to onboarding HINTS only; a plugin owns its own forms/locale/validation (the
  CIUS tighten-in-own-gate pattern the prior-art review validated) so the tam.forms overlay
  registry + validator-set abstraction are dropped; the structured-address host superset
  survives as the one host-field presentation case. The review surfaced a MUST-SOLVE blocker:
  packaged-field data (ROT amounts, Skatteverket-auditable) must survive its plugin via a
  retirement tombstone (D-V8). Heavy lifecycle machinery (cascade/suspend, auto-activation,
  entitlement coupling) deferred. Decisions rewritten D-V1–V9; six shippable slices (1 =
  address superset, 2 = relationship model L1, 3 = real SE plugin set + tombstone, 4 =
  plugin-on-plugin L2, 5 = retroactive package, 6 = pack-shipped host-field presentation only
  if a 2nd consumer appears). Still design only; nothing built.
- **Framework batch 3: RequiresView kind-compat (the docs/31 deferral, closed)**:
  `RequiresView` now speaks the same `"name[:kind]"` grammar as `PublishesEvent`
  (`ContractKinds`, one parser for both sides), and the typed path pays nothing for it —
  `RequiresView<TRow>` derives each field's kind from the facade record's CLR property
  type, so generated requirements are kind-checked without the author writing a kind.
  PLG008 compares every declared kind against the live view's result field at Build()
  ("requires field 'id' of view 'things.list' as 'decimal' but the view exposes 'guid'");
  string stays the open end of the grammar — no declared kind, no check — mirroring
  PLG009's agreement-not-presence rule. Build-time only: manifest, contract and tam.ts
  byte-stable. Verified: suites 192+38 (new PLG008 mismatch test), wire 16+18+22+31 on
  fresh SQLite; Postgres skipped deliberately — the check has zero query surface (it runs
  before any database exists).
- **Framework batch 2: streaming uploads (autonomous, "go ahead I'm afk")**: the 5 MB
  base64 bound falls to STAGE-THEN-INTEND. A multipart staging endpoint
  (`POST /api/documents/staging`, documents.add-gated, 50 MB cap, own transaction)
  content-addresses raw bytes into IDocumentStore and returns the hash;
  `documents.upload` gains an optional `contentHash` (exactly one of base64/hash carries
  content — base64 became optional, additive D4) so the WRITE still rides the full
  pipeline: authorization, the folder-visibility predicate, audit, idempotency. An
  unstaged hash fails closed (documents.invalid-content); an unused staged blob is inert
  (content addressing dedupes; sweeping is the retention janitor's seam, noted deferred).
  Client: `TamClient.upload` (multipart through the same auth/retry path); FE: the new
  `file-staged` renderer sits ON the hash field — stages, carries the hash, falls back to
  the base64 sibling if staging is unavailable; the upload form switched to it. Verified:
  suites 191+38, additive baseline (+types/contract), wire 16+18+22+31 on fresh SQLite
  AND fresh Postgres (documents +5: staging returns hash, 403 without documents.add,
  upload-by-hash, staged round-trip, unstaged-hash fails closed), and a real file
  uploaded through the staged renderer in the browser (screenshot). docs/36 updated.
- **Share dialog: the picker holds the selection (user-directed: "shouldn't the picker
  have the selection")**: the folder share dialog collapsed from list-plus-form (two
  "Delas med" labels, a submit button) into ONE MultiSelect — the pills ARE the folder's
  own grants (described labels from documents.folders.shares), picking adds (share
  intent), removing a pill revokes (unshare intent), each immediate; current grants are
  unioned into the option data so pills always render labeled, options come
  server-searched and kind-grouped from reach.search exactly like the form renderer.
  FE-only (DocumentsBrowser). Live-verified: add-through-picker persisted and re-opened
  as two pills (technician + Tekla Nilsson); documents suite 26/26 on fresh SQLite;
  server untouched since the fully-matrixed 095242b.
- **Framework finish, batch 1 (user-directed: "finish the framework stuff before more
  domain")**: three deferred items closed. D-R6 REACH DESCRIBE — `IReachProvider` gains
  `DescribeAsync` as a DEFAULT interface method (null = no label; existing providers keep
  compiling), `ReachResolver.DescribeAsync` is fail-SOFT where containment is fail-closed,
  all four providers describe (user → account name, role → its own name, tenant → node
  display name, approvals.group → group name), and the documents shares view labels each
  grant server-side (additive `label` field) — the share dialog now shows "Tekla Nilsson"
  instead of a guid. RULE-PICKER disambiguation — the trigger dropdown resolves
  `operations.{id}.name` → title → id, and `.name` keys were added exactly where bare
  titles collided (Godkänn ×2, Inaktivera ×2, Avveckla regel ×2; found by scripted title
  audit across all six catalogs). GRID.TOTAL plural — "1 träffar" fixed by the
  count-prefixed form ("Antal träffar: {count}"), sidestepping a plural engine.
  Verified: suites 191+38, additive baseline (+types/contract), wire 16+18+22+26 on
  fresh SQLite AND fresh Postgres (documents suite +1: the described-grant label),
  share-dialog screenshot with a named grant. Remaining framework queue: streaming
  uploads, RequiresView kind-compat, then the variability seams (tenant profile + form
  overlays); domain arcs (participants/multi-day/address) deliberately parked behind them.
- **Grid feature round (user-directed: "needs a better frontend overall to support many
  columns")**: ViewGrid reworked for wide grids. Row actions COLLAPSE into a per-row ⋯
  menu when there is more than one (the merged orders grid had seven inline buttons —
  most of its width); a single action stays a button. A COLUMN CHOOSER (toolbar
  "Kolumner" menu, declared + extension columns alike) persists per grid in localStorage;
  the MODEL curates the default via `grid.Column(x => x.Foo, defaultHidden: true)` — a
  new additive manifest field (`defaultHiddenColumns`), demonstrated on the orders grid's
  Type column. Filters collapse behind a "Filter" toggle with an active-count badge
  (open stays forced while filters are active). Cells and headers are nowrap with
  ellipsis (maxWidth 280) and the ScrollContainer's minWidth scales with column count —
  wide grids now SCROLL sideways instead of squeezing badges into "Ö…"; sticky header +
  compact vertical spacing. New chrome keys grid.columns/filters/row-actions (sv+en).
  Verified: suites 191+38, additive baseline (+types/contract regen — byte-stable),
  wire 16+18+22+25 on fresh SQLite AND fresh Postgres, screenshots (compact grid with
  whole badges, column chooser, row-action menu).
- **Order/WorkOrder MERGED into one entity (user decision: "it should be modeled under a
  single entity — splitting doesn't make sense")**: the commercial commitment and its
  execution are now the same Order. The aggregate absorbed Priority, ScheduledDate,
  AssignedToActorId/Name (snapshot) and the execution machine — one lifecycle Open →
  Scheduled → InProgress → Completed | Cancelled (Done/Closed collapsed; completion legal
  from any live state so a pure sales order needs no scheduling; makulera from every
  non-terminal). New intents orders.schedule (date+assignee in one intent, lookup-picked,
  membership-validated), orders.assign, orders.start (own-scope paired), orders.set-priority
  (EDIT001 window Open|Scheduled). Time and materials rebound to the order (wire
  orderNumber; booking blocked on terminal states); the record page carries Detaljer / Tid
  / Material / Dokument / Egenkontroll / Fakturering — the whole engagement in one place;
  field mode "Mina ordrar" = the same orders page own-scoped. Invoicing unified: ONE
  subscriber on order-completed drafts from ACTUALS (approved time + materials) and falls
  back to the estimate when nothing was booked; Invoice.WorkOrderId dropped;
  work-order-completed retired. The seeded urgent-schedule-window rule and the
  requiresLift custom field moved to the order. The `work-orders.*` wire surface is GONE —
  an intentional D4 break, re-baselined in the same commit by owner decision (manifest
  66 ops / 45 views; types + contract regenerated). docs/34 carries the supersession
  note; docs/32/22/14 + tutorial samples synced. Deferred follow-up: time PLANNING
  (calendar/capacity board over ScheduledDate+assignee) as its own arc. Verified: suites
  191+38 (OrderPriorityTests port, merged-machine pipeline test publishing
  order-completed), wire 16+18+22+25 on fresh SQLite AND fresh Postgres (rules-gating
  ported to orders.schedule/set-priority), screenshots of the merged grid and the
  order's Tid tab.
- **Agent tooling in the repo (user-directed: "save that in the repo and perhaps make a
  skill… so agents can find their way around")**: the session-local operational knowledge
  became repo artifacts. `scripts/snap.mjs` — the Playwright driver as CLI + library
  (auto-detected chromium, the login traps absorbed: novalidate on the credential form,
  conditional tenant picker with exact-match labels, Mantine locator gotchas), documented
  in the **app-snap** skill. The four wire suites moved into `verify/`
  (field-service/rule-builder/rules-gating/documents + `all.mjs` runner that refuses to
  run without a live host and warns the suites are NOT idempotent). The **verify-loop**
  skill writes down the whole develop-and-verify loop: solution-build gotcha,
  build-server shutdown after generator changes, the generated trio with absolute paths,
  additive baseline semantics, fresh-DB matrix one-liners for BOTH engines, the pkill
  exit-144 trap, per-milestone wrap expectations. CLAUDE.md points at all of it.
  Smoke-verified: `node verify/all.mjs` green (16+18+22+25) against a fresh SQLite host,
  snap CLI produced order-documents-tab and admin-mode shots.
- **Tenant-in-URL deep links (autonomous, per the communicated order)**: the URL grammar
  grew `?tenant=` (the ACTING node — written by the company picker, seeded into App state
  on load after validation against the standable set; the server still validates the
  act-as header on every request, so the URL is a wish, not a grant) and `?tab=` (the
  active record tab). A cross-company subtree row now writes its tenant alongside
  `?record=`, so the deep link re-establishes the scope the record LIVES in on reload —
  previously the per-call actAs was lost and the record vanished. Subordination rules:
  page clears record, record clears tab, closing a record restores tenant to the global
  acting node; Back/forward drive all of it (App popstate for tenant, ModelPage for
  record+tab). An order's documents tab is now a bookmarkable place — the order-grid docs
  affordance can become a plain link when a RowLink-style model concept is wanted
  (deferred, needs a model-surface decision). Playwright-verified 6/6: picker writes
  tenant; record+tab+tenant compose; cold reload restores record page, documents tab and
  acting company (Demo AB ▸ Nord); Back keeps scope. FE-only: manifest/baseline/contract
  byte-identical, .NET suites unchanged since previous commit; wire 16+18+22+25 re-run
  green on fresh SQLite AND fresh Postgres.
- **Swedish copy pass (user-directed: "besiktning… not sure it's the right meaning";
  "the Save button… might need multiple labels")**: a native-register audit (focused
  subagent) applied across all six catalog families. The A-fix: the inspect capability is
  EGENKONTROLL, not besiktning (a statutory third-party inspection) — plugin title, tab
  heading, extension-field label renamed (en: Checklists); checklist items are PUNKTER,
  not rader. The B-fix (mechanism): one `operations.{id}.title` was landing on six
  surfaces; tam-react now resolves convention keys with fallback (`tOr`): submit button →
  `.submit` → `actions.save` for EDIT forms (any changeSet field — every edit form reads
  "Spara" for free) → title; grid buttons → `.action` → title. Create ops retitled
  "Ny/Nytt X" + `.submit: "Skapa"` (auto-added for every Ny/Nytt/New title, sv+en parity
  kept). C/D-fixes: godkännande (not attest), avveckla (not avsluta) for retire,
  färdigställ faktura (frees slutför for orders), överordnad (not förälder), köa om +
  stoppad post (not "köa död post igen"), abonnemang (not plan), replay-actor message no
  longer says "beställare" for a request submitter, bolag-branded tenant error overrides
  in the ERP catalog, RuleBuilder's hardcoded "is empty"/"is set" moved to catalog keys
  (the Swedish error text finally matches the operators on screen), dev-portal hint
  de-calqued. Conventions written into docs/29 (label surfaces + terminology decisions).
  Deferred: `.name` disambiguation for the rule-trigger picker (colliding bare
  imperatives), grid.total pluralization. Screenshots: "Ny order"+Skapa modal, record
  page with Spara + Egenkontroll tab. Verified: suites 191+38, additive baseline
  (types/contract byte-identical — locale-only), wire 16+18+22+25 fresh SQLite AND
  Postgres.
- **Documents nav cleanup (user-directed: "documents and document folders as nav pages
  seems a bit weird")**: the tam.documents package no longer suggests flat admin pages —
  documents surface where they belong: the record's documents tab and the work-mode tree
  browser. Folder SHARING folded into the browser: a share dialog listing the selected
  folder's OWN grants (new `documents.folders.shares` view, documents.manage like the
  intents) with one-click revoke, plus the reach-picker share form; inherited/open access
  is the empty-state hint (the effective-ACL question stays server-side). Framework
  refinement: `nav.None()` — declaring nav, even empty, graduates a plugin past the
  mechanical More-page (docs/30 D-N1 sharpened; previously graduation required ≥1
  contribution, so a deliberately nav-less package would have resurrected its grids under
  "Mer"). Grids stay declared (wire names permanent). Screenshots: share dialog with
  revoke, clean admin nav. Order-GRID docs affordance deliberately deferred to the
  tenant-in-URL deep-link arc (a row action jumping to the record's documents tab).
  Verified: suites 191+38, manifest additive (baseline + types + contract regenerated),
  wire 16+18+22+25 on fresh SQLite AND fresh Postgres (documents suite grew 3 checks:
  shares list, 403 for non-managers, empty after unshare).
- **The reach picker (autonomous, user-authorized "proceed as you see fit")**: docs/35
  D-R5 closed — the seam's last rough edge. The tam.reach package (FIFTEENTH) ships the
  reach.search view (permission reach.search): every registered provider's SearchAsync
  aggregated per request — activation-gated and fail-closed, so the picker offers exactly
  the sets a stored ACL could resolve. The documents SHARE form's Reach field now renders
  as the new `reach` picker (tam-react): grouped by kind with localized group headers
  (reach.kinds.{kind} keys shipped by each kind's OWNER — framework kinds in the package
  defaults, approver groups in the approvals plugin's catalogs; unknown kinds fall back to
  the raw token), server-side debounced search, value = the canonical ReachRef string. No
  more typing raw refs. Screenshots delivered (share form + open grouped picker).
  Verified: suites 191+38 (fifteen-packages test), manifest additive (baseline + types +
  contract regenerated), wire 16+18+22+22 on fresh SQLite AND fresh Postgres.
- **Generated symbols, completed (user-directed: "finish the generated symbols stuff")**:
  every section of the host contract artifact now generates symbols, and the samples'
  last host-vocabulary strings died. The artifact gained a grids section (the D-X1
  grid-action targets, PLG010-filtered); the generator emits HostContract.Operations
  (CONSTS — usable in attributes: [Gate(HostContract.Operations.OrdersComplete)]),
  HostContract.Slots (SlotContractRef with context keys), HostContract.Entities
  (EntityContractRef) and HostContract.Grids (GridContractRef); PluginBuilder gained the
  matching Panel/ExtensionField/GridAction overloads; subscriber attributes ride the event
  facades' consts ([OnEffect(OrderCreatedEvent.EventType)]). inspect + invoicing migrated:
  gates, subscribers, packaged fields, panels and the grid action all reference symbols —
  the ONLY strings left in a plugin are its own ids (its vocabulary to name). Verified:
  suites 191+38, manifest deep-equal (authoring sugar only), artifact regenerated (CI
  freshness gate covers it), wire 16+18+22+22 on fresh SQLite AND fresh Postgres.
- **The developer portal + fully typed requirements (user-directed: "view it in the web
  app… its own mode tab" / "strings for view properties still smells")**: BOTH landed.
  (1) The last strings died: RequiresEvent<OrderCompletedEvent>() derives the event id,
  fields AND kinds from the generated facade record; RequiresView<OrdersDetailRow>(r =>
  r.Id, r => r.Number, r => r.EstimatedTotal) selects the read whitelist BY LAMBDA through
  the facade — a renamed host field is now a compile error at the selection site; inspect +
  invoicing migrated; manifest deep-equal proves behavior identical. (2) tam.developer —
  the FOURTEENTH framework package: the developer.contract view (permission
  developer.read) serves HostContractExport over the live model; the ERP host adds a
  DEVELOPER nav mode ("Utvecklare" tab) with the portal page (samples/web
  DeveloperPortal.tsx, the app's second registered page) rendering events (kinded payload
  cards), the views catalog (accordion, permission badges, kinded field chips), slots with
  context keys, extensible entities and the operations catalog — every heading and hint
  from locale catalogs (sv/en package defaults). Discoverability now has three
  synchronized forms of ONE contract: the artifact file (build input), the HostContract
  symbols (IntelliSense), the portal (running app). Playwright screenshots delivered
  (mode tab + events, expanded views, slots/entities/operations). Verified: suites
  191+38 (fourteen-packages test updated), manifest ADDITIVE (baseline + types + contract
  regenerated: new view/nav/permission), wire 16+18+22+22 on fresh SQLite AND fresh
  Postgres.
- **The host contract artifact — contract arc slice 3, the finale (user-directed, shaped
  by "how does a plugin author know what's available to extend?")**: `dotnet run --
  contract` exports the host's EXTENSION SURFACE as one versioned file
  (samples/erp/host-contract.json): consumable events (fields+kinds), consumable views
  (fields+kinds+permission), slots with context keys, extensible entities, gateable
  operations — filtered to what PLG010 permits (host + framework packages, never another
  plugin's). A plugin references it as an AdditionalFile and Tam.Compiler generates the
  ENTIRE surface into IntelliSense: a typed facade per event/view (sourced from the
  ARTIFACT now — the declaration-derived facades remain only as the artifact-less
  fallback) plus the HostContract index —
  plugin.RequiresEvent(HostContract.Events.OrderCompleted),
  plugin.RequiresView(HostContract.Views.OrdersDetail, "id", "number",
  "estimatedTotal") — ids, fields and kinds ride the artifact, NOTHING re-typed; events
  required whole, view subsets stay explicit (minimal read whitelist). inspect + invoicing
  migrated as proof; a MiniJson reader keeps the generator dependency-free; CI gains the
  artifact freshness gate (re-export + compare, like the manifest baseline). PLG008/9/10
  still verify everything against the REAL composed host at Build() — the artifact is
  compile-time convenience, Build() is the truth. DISCOVERABILITY IS THE ARTIFACT: the
  json is what an author browses, the generated symbols are what they type. The arc's
  end-state scorecard is now real: ONE hand-written declaration (the host's event/Result
  record); publish site, manifest, artifact, facades, requirement registrations and the
  composition check all derived or verified. Deferred: RequiresView kind-compatibility vs
  live wire kinds; generated symbols for slots/entities/operations. Verified: suites
  191+38, manifest deep-equal, wire 16+18+22+22 on fresh SQLite AND fresh Postgres.
- **Events are records — contract arc slice 2 (user-directed: "let's do 113 now")**: the
  last stringly contract in Tam is gone. Every published event is now a [DomainEvent("id")]
  payload record beside its aggregate (OrderCreated/OrderCompleted/OrderCancelled,
  WorkOrderCompleted, ChecklistPassed, InvoiceCreated/Finalized/Paid,
  ApprovalRequested/Approved) — the contract's fields AND kinds DERIVE from the record's
  members (AddEventType; CLR→kind map keeps string undeclared so record-derived and
  string-declared contracts are identical on the manifest), compile-time discovery
  registers them (the AddDiscovered idiom), and the nine hand-written PublishesEvent
  string declarations retired from the samples (the string form stays for dynamic
  publishers — tenant automation rules). Publish sites are COMPILE-CHECKED:
  new EventPublished(new OrderCompleted(...)) reads the id from the attribute, and the
  direct-outbox path got the same typed twin (Db.Publish(record) — the approvals park
  seam uses it). TAM009 closes the folklore hole: an ANONYMOUS event payload — effect or
  direct publish — is a build error; its first run flagged all ten anonymous sites, all
  migrated. The three edges of the event contract are now all verified: declaration
  (derived), publish site (compiler), consumption (PLG009 with kinds). Verified: suites
  191+38, manifest DEEP-EQUAL to baseline (the derived contracts are exactly what the
  strings declared), wire 16+18+22+22 on fresh SQLite AND fresh Postgres — the
  order-created → checklist-instantiation and magic-folder chains run through the typed
  records end to end.
- **PLG010 — the dependency line becomes mechanical (user probe: "what if a plugin
  extends a plugin?")**: the probe exposed that docs/22's rule (plugins depend on the
  HOST's contract, never on each other) was DOCTRINE, not a gate — PLG008/PLG009 verified
  existence and fields but not OWNERSHIP, so a plugin could quietly require another
  plugin's view or event and build green. Now: a plugin-tier RequiresView / RequiresEvent /
  OnEffect target must be owned by the host, by a framework package (always-on, host-like
  trust), or by the plugin itself — anything else is build error PLG010, with the
  sanctioned escape spelled out in the message (promote the shared concept into the host,
  or merge). Packages are exempt as consumers; the "*" wildcard is untouched. Three
  poacher tests (event-require, view-require, subscribe) prove each seam. docs/22 gains
  "Plugin-on-plugin: the tier that isn't (yet)": the contract-artifact mechanism
  generalizes verbatim to plugin providers (the host was only ever the FIRST provider) —
  what is genuinely new and deliberately undesigned is EXISTENCE semantics (DependsOn
  edges, activation cascade, entitlement coupling, self-owned slots); PLG010 is the
  placeholder for that design commit, not a refusal of it. Verified: suites 191+38,
  manifest byte-identical, wire 16+18+22+22 on fresh SQLite AND fresh Postgres.
- **Contract artifact, slice 1 — the publisher owns the shape (user-directed: "can the
  plugin just reference the manifest?")**: the answer is yes, and this slice builds the
  substrate. PublishesEvent now takes the same "name[:kind]" grammar as the requirements
  (ContractKinds — ONE parser for both sides; a typo'd kind is an error, never a silent
  string); the manifest's events section carries kinds (additive, baseline + types
  regenerated) — the manifest is now a complete machine-readable contract artifact; PLG009
  checks kind AGREEMENT (published guid required as decimal = build error) on top of the
  existing presence check; all nine sample publishers migrated to kinded declarations.
  NEW REPO SKILL .claude/skills/domain-design (user-directed): the aggregate design pass
  (classify root/owned-child/plain-row, place invariants, shape checklist, EF gotchas) that
  MUST run before a new domain is written — domains are designed, never fast-forwarded;
  CLAUDE.md points at it. Next slice (#113): plugin projects reference the host manifest as
  an AdditionalFile and the generator emits facades + requirements FROM the artifact —
  id-only Requires, shapes from the snapshot, still Build()-verified against the real host.
  Design written into docs/31 ("The manifest is the contract artifact"). Verified: suites
  191+38 (new PLG009 kind-mismatch + typo-kind + manifest-kinds assertions), manifest
  additive over baseline, wire 16+18+22+22 on fresh SQLite AND fresh Postgres.
- **TAM008 — the anemic-entity build gate (user-prompted: "how do we ensure better DDD")**:
  the domain-shape convention got the same teeth as every convention that actually holds
  here. New analyzer rule: an ITenantScoped class declared under a Domain/ directory with
  a MUTABLE PUBLIC SETTER is a build error — the Domain/ path IS the declaration "this is
  guarded state" (docs/29), so intent methods + private set + factory become the compiler-
  checked default for every future aggregate, in the IDE before the app boots. Init-only
  properties pass (immutable-after-create is not anemic); IVersioned.Version and
  IExtensible.Extensions are exempt (pipeline-stamped contracts); genuinely plain rows opt
  OUT visibly — #pragma warning disable/restore TAM008 with a reason comment, greppable
  like the python-check allowlists. First run flagged exactly the three deliberate plain
  rows (ApprovalGroup / ApprovalGroupMember / ApprovalRule — now annotated with their
  reasons) and nothing else: the erp, inspect and remaining approvals domains are clean,
  which doubles as the negative test. Enforcement map (docs/29) + CLAUDE.md updated.
  Verified: suites 191+38, manifest byte-identical, wire 16+18+22+22 on fresh SQLite AND
  fresh Postgres.
- **Aggregate ownership pass (user-flagged)**: the inspect sample's roots now OWN their
  lines — the DDD gap the checklist-template sampling exposed (items were free-floating
  rows keyed by parent id; every invariant lived in operations as cross-row queries).
  ChecklistTemplate gained an Items navigation with root intents: AddItem (position
  sequence + retired guard on the root) and Instantiate — the template is the checklist
  FACTORY, stamping title, mandatory flag and every line in one call (subscriber and seed
  both go through it). Checklist gained Items with guarded Pass (refuses while own lines
  are open — the invariant moved INTO the aggregate now that the root owns the lines),
  Check(itemId) (last line closing passes the checklist in the SAME transition) and
  Uncheck(itemId) (re-opens in the same transition); item mutators went internal, so a
  passed checklist with an open line is unrepresentable through the domain surface.
  Operations shrank to load-root-with-Items + intent; findings moved beside their
  aggregates (ERP idiom); views/gate read the navigation. Boundary kept: approvals group
  MEMBERS stay flat — join rows to external actors are references, not owned children
  (convention + the EF tracked-root/Add gotcha written into CLAUDE.md). Verified: suites
  191+38 (six InspectV2 tests surfaced the EF gotcha — a child discovered on a tracked
  root reads as an existing row; fixed with the explicit Add), manifest byte-identical,
  wire 16+18+22+22 (fieldm6 revived: its project-order check predated arc 4b's
  project-belongs-to-customer guard — suite fixed to pair the project with its own
  customer) on fresh SQLite AND fresh Postgres.
- **Docs/tutorial sync (user-prompted)**: the recent arcs written down where readers look.
  NEW docs/36-documents.md — the tam.documents design in full (path-tree folders,
  override-not-union reach ACLs behind the one visibility predicate, content-addressed blobs
  behind IDocumentStore, 404-not-403 downloads, magic folders + DOC001, the record documents
  tab and the browser page; decisions D-DOC1..6) — wired into the site nav and llms.txt;
  docs/04 gained the async `Task<IQueryable<Result>>` Execute form; docs/22's package-tier
  count corrected to thirteen with tam.documents named; docs/35 links its first consumer.
  Tutorial drift fixed against the running sample: the per-aggregate split reflected in the
  index file tree, step 1 and the tally (with the CI-enforced convention stated); step 1's
  OrderErrors→OrderFindings rename and `Money?` type; step 17's contract declarations now
  carry `:kind` suffixes and point at the generated facades. Stale header counts here
  (twelve→thirteen packages, 149→191 tests) refreshed. Verified: check_docs +
  check_structure green; docs-only — no wire surface touched.
- **Generated typed contract facades (user-approved)**: the TS-client pattern applied to
  plugin authoring — a plugin's own `RequiresEvent`/`RequiresView` declarations now generate
  internal typed facades (Tam.Compiler syntax provider over the fluent calls' literal args):
  `OrderCompletedEvent.From(effect).Number` replaces `effect.String("number")`, and
  `TimeListRow.From(row).Amount` replaces `row.Decimal("amount")` — compile-time names, same
  PLG008/PLG009-checked wire contract, zero host-CLR references (facades are generated from
  the plugin's OWN declaration into its OWN assembly). An optional `:kind` suffix on
  declared fields (`"estimatedTotal:decimal"`, default string) types the property; the
  builder strips it before the wire/whitelist sees the name — no name sniffing, kinds are
  DECLARED. Inspect and invoicing handler reads migrated as the proof (approvals declares
  no foreign contracts — nothing to migrate). Deferred with intent: contract-side kinds on
  PublishesEvent (requirements type what they read; the contract still owns names).
  Verified: suites 191+38, manifest byte-identical (compile-time sugar only), structure +
  docs checks, wire 18+22+22 on fresh SQLite AND fresh Postgres — the magic-folder +
  checklist-instantiation chains now run THROUGH the facades on the wire.
- **Sample structure + DDD pass (user-directed)**: the convention drift fixed at the root —
  in three commits, each proven behavior-identical. (1) MECHANICAL SPLIT: inspect (395-line
  Features.cs) and approvals restructured to Domain/<Aggregate>.cs + Features/<Aggregate>.cs
  per aggregate; the monolithic findings classes split per aggregate (internal names only,
  wire codes unchanged); invoicing already complied (single aggregate). (2) DDD HYGIENE
  where invariants exist: inspect's Checklist/Item/Template got private setters + intent
  methods (Pass/Reopen, Check/Uncheck, Retire — the cross-entity open-items invariant stays
  in the operations, which need the database; the ERP idiom); approvals' ApprovalRequest got
  a Park factory and ATOMIC decision transitions (Approve/Reject/CloseOut — no half-decided
  request is representable); genuine config/join rows stay plain data. (3) THE GUARDRAIL:
  scripts/check_structure.py in CI — line cap (~400) and one-wire-prefix-per-file over
  samples/ and Packages/, allowlisted exceptions carrying reasons. Its FIRST RUN caught the
  ERP host's own 467-line Domain.cs — now split into Domain/<Aggregate>.cs (8 files
  mirroring Features/ and the ErpModel fragments) — plus the Seed (allowlisted: one demo
  narrative) and the Vault's two claimed prefixes (allowlisted: one area). Every commit:
  manifest deep-equal against the pre-pass export, suites 191+38, wire 18+22+22 on fresh
  SQLite AND fresh Postgres. CLAUDE.md and the docs/29 enforcement map now state the rule
  AND its enforcement — the canon, the convention, and the check agree.
- **Beauty arc 5, part 3b — the documents FE (arc 5 CLOSES)**: three surfaces, one new bind.
  (1) THE ENTITYREF RECORD BIND: `bind.QueryEntityRef("attachedTo", "order")` fills a record
  tab's grid param with the open record's own IDENTITY as a canonical EntityRef (wire
  `$ref:order`; the client composes "order:{recordKey}") — the documents tab filters on WHICH
  record, not one of its fields; PAGE001 validates the entity key. The orders record now
  reads Detaljer / Dokument / Besiktning / Fakturering. (2) THE TREE BROWSER is the app's ONE
  registered React page — the docs/32 D-P2 escape hatch used as intended for genuinely custom
  UX (two-pane tree + file list), composed ENTIRELY from framework capabilities: useView over
  the ACL-filtered listings, OperationForm for the folder/upload intents, authorized
  downloads via the new `client.blob()` (bearer-carried; an <a href> cannot). Placed in
  work-mode nav by the host with its explicit permission (NAV005 for registered pages); the
  package's suggested admin listing renamed to documents.list (NAV001 caught the collision).
  (3) THE "file" RENDERER: a real file input carried as base64 in the wire field, filling the
  upload contract's fileName/contentType siblings through the renderer setField seam.
  Deferred with a STATUS note: per-kind PackageDocument contracts (no small honest version
  exists — per-kind contracts need real demand to shape them; revisit when a plugin ships
  document kinds). Verified: suites 191+38, manifest additive (documents tab + $ref bind +
  nav node), regen types, wire 18+22+22 on fresh SQLite AND fresh Postgres, Playwright — the
  browser lists the tree and files, the order record's Dokument tab shows the seeded attached
  instruction (screenshots 5-01/5-02), zero page errors. Arc 5 complete: reach seam →
  documents core → magic folders → FE.
- **Beauty arc 5, part 3a — magic folders (event-driven tree materialization)**: the host
  declares `.DocumentFolder("order-created", "/order/{number}")` and every created order gets
  its document folder — no handler learns about documents. DOC001 verifies the binding at
  Build (the event is declared; every `{placeholder}` names a payload field of its contract).
  Delivery is ONE wildcard effect subscriber in tam.documents (subscriber `EventType = "*"`,
  the GateDefinition.Wildcard precedent extended to subscribers — dispatch and PLG009 both
  exempt it) reading the model's bindings — which events materialize folders is model data,
  never per-event registration. The subscriber renders the template from the payload, skips a
  binding whose placeholder rendered empty (never file into a collapsed path), and mkdir -p's
  through the SAME `EnsureFoldersAsync` helper the define intent now delegates to —
  idempotent under at-least-once delivery. Verified: suites 191+38 (DOC001 cases + carriage),
  manifest byte-identical (server-side behavior only), wire 18+22+22 on fresh SQLite AND
  fresh Postgres — the documents suite now creates an order over the wire and polls the
  folder tree until `/order/{number}` appears via the outbox. Remaining for part 3b: the FE
  tree browser + record documents tab + Playwright, then #106 closes.
- **Beauty arc 5, part 2 — tam.documents core (the reach seam's first consumer)**: the
  THIRTEENTH framework package — the tenant's document tree, model-driven end to end. Folders
  are a materialized path-tree (the tenants-table idiom; `documents.folders.define` is
  mkdir -p as an intent, un-retiring on redefine); documents carry file metadata + an
  optional `AttachedTo` EntityRef ("order:8f3c…" — the record-tab query, indexed); content
  is CONTENT-ADDRESSED (SHA-256) behind the `IDocumentStore` seam with a DB-blob default
  (tenant-scoped, so the ambient filter and RLS cover bytes exactly like metadata; TryAdd —
  a deployment swaps in S3 without touching metadata or ACLs). FOLDER ACLs ARE THE REACH
  SEAM IN ANGER: `documents.folders.share/unshare` store canonical ReachRef strings (an
  unknown kind is a teaching finding; an INACTIVE plugin's kind stores as inert data that
  reaches nobody and revives on activation — D-R3 proven on the wire); visibility is ONE
  predicate (`DocumentAccess.VisibleFolderIdsAsync`, docs/28 discipline) — effective ACL =
  own rows, else nearest ancestor's (OVERRIDE, so a child locks tighter than its parent),
  else open; `documents.manage` administers the tree; views filter reads through it, upload
  re-checks it, and the download endpoint 404s out-of-reach content (no existence leak).
  Upload rides the STANDARD operation pipeline (base64-bounded 5 MB — streaming is a
  deliberate deferral); download streams from one endpoint. To make the ACL-filtered views
  possible the VIEW EXECUTOR grew async views (`Task<IQueryable<Result>>` Execute — awaited
  at the one invoke point; Result typing unchanged), and RowForm prefill gained the
  row-action id fallback (`folderId → row.folderId ?? row.id` — one prefill convention for
  both action modes). Seeded: /avtal (dispatcher-only via role reach), /instruktioner
  (open), one instruction ATTACHED to an order. Verified: suites 188+38, manifest additive
  (exactly 5 ops + 2 views + 4 forms + 2 grids), regen types, wire 18+22 plus a NEW 20-check
  documents suite (role-reach hiding, doc-follows-folder, attachedTo query, mkdir -p,
  upload/download round-trip, ACL inheritance + override + unshare-restores, unknown-kind
  finding, inert inactive-plugin ref, retire) on fresh SQLite AND fresh Postgres. Deferred
  with intent: streaming/multipart upload, magic folders via event bindings, per-kind
  PackageDocument contracts, and the FE tree browser + record documents tab (part 3).
- **Beauty arc 5, part 1 — the REACH seam (docs/35)**: the vocabulary for naming a people-set
  ON A ROW — the object-side question the capability axis deliberately does not answer, and
  the prerequisite for the documents domain's folder ACLs. Designed against docs/27/28 and
  kept inside their settlement: reach is a DOMAIN-side membership test, never a new principal
  type (no change to actor resolution, the flat grant set, the manifest, or TAM00x). Shipped:
  `ReachRef` (canonical `kind[:id]` string — storable in one ACL column), `EntityRef`
  (`entityKey:guid` — the typed cross-entity reference documents will attach by),
  `IReachProvider` (ctor-DI class answering containment + picker search, the gate idiom),
  model registration with REACH001 (kind grammar, uniqueness, plugin-prefix rule — mirroring
  PLG001), `ReachResolver` (parse → model lookup → ACTIVATION GATE for plugin kinds →
  activator → delegate; fail closed at every step, so an ACL naming an inactive plugin's
  group is inert data that revives on re-activation), the three framework kinds over facts
  the framework owns — `user`, `role` (node-local; the cascaded-role question is deferred as
  the docs/27 cross-level name-resolution question), `tenant` — and the PLUGIN-side proof:
  the approvals plugin registers `approvals.group`, whose containment IS its existing
  effective-approver resolution (nested groups), so host domains can reference approver
  groups without learning group semantics. The manifest carries nothing yet (D-R5): reach
  surfaces to the client as a view over SearchAsync when the documents ACL editor lands.
  Verified: suites 188+38 (ref grammars, REACH001 cases, model carriage), wire 18+22 on
  fresh SQLite AND fresh Postgres, manifest byte-identical to baseline (seam-only commit,
  no UI surface — provider containment over live membership rows gets wire coverage with the
  first consumer, the documents domain).
- **Beauty arc 4b — ERP idiom uniformity around the order detail**: the sample now practices
  its own conventions end to end. (1) `OrderErrors` → `OrderFindings` — the last `*Errors`
  finding class renamed to the idiom every other aggregate already used. (2) The order status
  machine got its OTHER exit: `orders.cancel` (entity-guarded both directions — a completed
  order never cancels, a cancelled one never completes, each with its own teaching finding),
  paired-atom scoped (`orders.cancel`/`-all`, dispatcher granted), publishing a declared
  `order-cancelled` event, riding the orders grid as a row action. `customers.deactivate`
  joined it (the stock.deactivate idiom: an INTENT and a retirement, not a deletion; strict
  owning-node scope; `customers.already-inactive` guard). (3) THE MODEL SPLIT BY DOMAIN:
  ErpModel became a partial class of per-domain fragments — `.AddOrders()`, `.AddCustomers()`,
  … `.AddMaterials()`, one file per domain mirroring Features/ — each declaring its page,
  forms and grid; the root keeps what spans domains (plugins, event contracts, nav). Proven
  purely mechanical: the exported manifest before and after the split compares deep-EQUAL.
  (4) THE MULTI-PLUGIN PanelTabs STORY IS NOW VISIBLE: seed activates invoicing for the demo
  tenant alongside inspect, so the order record page opens with Detaljer + TWO plugin tabs —
  Besiktning (inspect) and Fakturering (invoicing's invoice grid bound to the open order) —
  neither named by the host. Deferred with intent: `time.unbook`/`materials.remove` (waiting
  until a real correction story sizes them) and Stock deepening (QuantityOnHand/movements —
  a stock ledger is its own arc, not a grid column). Verified: suites 167+38, wire 18+22 on
  fresh SQLite AND fresh Postgres plus an 11-check arc suite (cancel machine both directions,
  deactivate guard, two-plugin slot expansion, manifest additive: exactly orders.cancel +
  customers.deactivate added), Playwright — order record page shows all three tabs, the
  Fakturering tab renders the plugin grid in record context, Makulera rides the grid rows,
  zero page errors.
- **Record display semantic + nav dedup (user-directed)**: two product calls. (1) THE RECORD
  DISPLAY SEMANTIC: a record now declares how substantial it is — `modal` (a quick edit over
  the grid) or `page` (a workspace: a routed full surface replacing the grid, back affordance,
  URL-native). Undeclared it DERIVES from structure — several tabs or any child grid make a
  workspace, a plain form/slot record stays a modal — with `.Display(RecordDisplay.Page|Modal)`
  as the override; the manifest carries `display`, and the client maps semantic → presentation
  (a future client could render `page` as a drawer with no model change). Orders and
  work-orders became routed pages automatically; customers/projects/stock stayed quick-edit
  modals; the derivation + override are tested. (2) NAV DEDUP: the standalone Tid and Material
  work-mode pages — pre-record-tabs artifacts whose listings now live as tabs INSIDE the
  work-order record (where time.approve still rides the grid) — are gone from work mode; the
  materials page declaration is deleted outright (its grid lives on as the record tab), the
  time page stays for the technician field mode's Min tid. Work mode reads Ordrar /
  Arbetsordrar / Projekt / Kunder / Artiklar / Checklistor, and the Material-vs-Artiklar
  (consumption-vs-catalog) confusion is dissolved rather than renamed around. Verified: suites
  167+38 (display derivation/override tests), wire 18+22 fresh SQLite AND Postgres, additive
  baseline (orders/work-orders records carry display "page", customers "modal"), Playwright —
  the order record renders as a full routed page (back button + tabs, no modal), Back returns
  to the grid, customers still opens the modal, field mode intact, zero page errors.
- **Operations own the input contract (docs/40) — Sol's canonicalization plan**: the architectural
  fix behind Finding 2, run as a phased arc. (1) DERIVATIONS ARE OPERATION-OWNED: resolved to their
  operation and validated at build (DER001–005), not discovered by an input type two operations
  might share; resolve/manifest look them up by operation id. (2) FORMS ARE PROJECTIONS: the
  every-form RequiredWhen scan is gone — a form's requiredness is TIGHTENING applied only when that
  form is named (the single operation endpoint takes an optional `?form=`; no second submit door, so
  MCP/integrations stay on the plain operation contract). (3) OUTPUTS CARRY THEIR OWN AUTHORITY:
  DerivationResult.Require(field, when, finding) is authoritative conditional requiredness — resolve
  shows the indicator, submit BLOCKS with the domain finding for EVERY caller; Suggest/warnings/
  option ordering stay advisory. RunDerivationsAsync is the one place resolve and submit evaluate an
  operation's derivations, so they can't disagree. (5a) SAMPLE MIGRATION: orders.create's
  project-required moved off the create form onto the operation (a Require() derivation preserving
  orders.project-required); rules.define's messages stays as genuine form tightening. Verified:
  suites 196 + 43 (FormBindingTests pin that the operation rule governs a direct AND a
  through-the-form call identically, while form tightening applies only through its own form), the
  full wire matrix GREEN on fresh SQLite AND Postgres (94 checks each). (4) CANDIDATE SETS ARE VIEWS,
  MEMBERSHIP IS AUTHORITATIVE: a derivation binds an operation field to a candidate View plus a base
  filter (`DerivationResult.Lookup(field, viewId, filters, invalid)`); on submit the picked key is
  validated by `ViewExecutor.ContainsAsync` — an `Exists` that reuses the view's activation,
  permission (fail-closed), tenant scope and existing `BindFilters` path, then adds the key predicate
  — so the rendered page is never proof of validity. The base filter reuses the view's EXISTING
  `Filterable` fields, not a bespoke query param (per the design correction): `projects.lookup` now
  projects a filterable `CustomerId`, closing the latent gap where the picker returned every
  customer's open projects. orders.create binds `ProjectId` to `projects.lookup` scoped to the picked
  customer, so a real-but-wrong-customer project is rejected at submit (`orders.project-not-available`).
  Verified: suites 196 + 45 (LookupMembershipTests pins cross-customer reject / same-customer accept;
  EventRuleTests actor gains `projects.read` since membership reuses the view permission), manifest
  additive-only (new filterable result field), full wire matrix GREEN on fresh SQLite AND Postgres
  (94 checks each). The docs/40 arc is now BUILT end to end.
- **docs/40 re-review — Operation Input Contract (9 findings, all confirmed against code, then
  fixed)**: Sol re-reviewed the canonicalization arc and found four correctness/security gaps plus
  incomplete parts. Landed in the recommended priority order. GROUP 1 (correctness/security): form
  binding fails CLOSED — a supplied `?form=` must exist, match the operation, and be from an active
  plugin, else rejected (no silent downgrade); the form binding is part of idempotency identity, so a
  direct call and a through-a-form call with the same body+key are independent records; blocking
  derivation findings (`AddFieldError`/`From`/error `Add`) are ENFORCED at submit for every caller,
  not just previewed at resolve. GROUP 2 (resolve/submit + lookup parity): `RunDerivationsAsync` runs
  ALL derivations every call (the changed-field skip is gone), so resolve's complete field state can't
  report a requiredness/membership submit contradicts; `ContainsAsync` validates every lookup
  base-filter key against the view's Query+Filterable fields and throws DER006 on a typo rather than
  silently widening the universe; lookup membership reads the key from the DESERIALIZED input (a
  `Change<T>` edit field unwraps correctly). GROUP 3 (architecture completeness): resolve returns a
  `ResolvedLookup` (view + base filters) the FE picker scopes itself by — the sample no longer
  materializes the candidate set as inline options; `ContainsAsync` reuses the read path's SubtreeRead
  widening (shared `ApplySubtreeWideningAsync`, execution-local restore); `RequireOneOf(field,
  options, invalid)` gives authoritative closed inline options (advisory `AddOptions` unchanged), and
  the docs/40 output table + a new "pipeline-rejects, not commit-stable" boundary section resolve the
  self-contradiction. Verified: suites 196 + 50 (ContractEnforcementTests via a new `ErpModel.Builder()`
  seam pins blocking-finding, idempotency-binding, bad-filter fail-closed, and closed-option
  enforcement; FormBindingTests updated to "mismatched/unknown form is rejected"), web typechecks,
  manifest unchanged (all runtime-additive), full wire matrix GREEN on fresh SQLite AND Postgres (94
  checks each). docs/40 is genuinely closed, not merely moved into a better model.
- **Sol re-review round — boundary + isolation hardening (all confirmed, then fixed)**: a static
  re-review of the fixes above surfaced remaining weaknesses. Two verification agents confirmed
  every claim against real code first. Shipped in three batches: (1) REQUEST-BOUNDARY FAIL-CLOSED —
  a malformed operation body now answers an invalid-input envelope not a 500; the executor
  normalizes an absent body for every caller; MCP guards non-object `params` and a missing
  `arguments` (no 500 past the JSON-RPC catch); a non-array integration payload answers
  `integrations.expected-array` (400) not an empty-results 200; the view executor fails closed on
  in-memory extension filters/sorts and on unknown query params (a typo'd `?statuz=` no longer
  returns the unfiltered set); a new MCP001 build gate rejects colliding tool names. (2) EF/PROVIDER
  SEAMS — the idempotency-record save uses the unique-violation classifier (a connection/FK fault is
  no longer misread as an idempotency race); the Postgres SQLSTATE classifier moved to a provider-
  core `AddTamPostgres`, independent of the optional SSE backplane; `OrderNumberSequence` is now
  `ITenantScoped`, so the global filter and RLS backstop cover it and the manual predicate is gone.
  (3) UNIT-OF-WORK ISOLATION — every rollback path clears the change tracker (a blocked attempt
  leaves no residue on its context), a sanctioned `ExecuteIsolatedAsync` is the named path for
  non-request callers, the inbox routes through it (mapping now inside the row scope), and a
  regression test pins that a blocked write-then-reject leaves nothing behind. Verified: suites 196
  + 40, the full wire matrix GREEN on fresh SQLite AND Postgres (94 checks each), and a Postgres
  concurrency probe (25 parallel creates → 25 unique, contiguous order numbers). OPEN: the deeper
  Finding-2 architecture — moving authoritative conditional requiredness off form bindings onto the
  operation/input model — is a framework-API decision left for an explicit call, since both existing
  cases are already enforced by their domain handlers with better findings.
- **Sol review round — 10 findings, all confirmed against real code, all fixed**: an external
  technical review of the newest surfaces. Four verification agents confirmed every finding
  before a line changed. Fixed top-down by severity: (1, Critical) INBOX ROLLBACK LEAK — one
  failed integration row's transaction leaked onto sibling rows; each record now runs in its own
  `PinnedScope` (fresh DI scope + DbContext, tenant-pinned). (2, High) CONDITIONAL REQUIREDNESS
  was resolve/client-only — a form-bypassing caller (MCP/integration/forged) could omit a
  `RequiredWhen` field; the SAME predicate is now authoritative at submit, coexisting with a
  richer domain gate (rules.define RUL003) where one exists. (3, High) GATE FREEZE comments
  overpromised isolation — corrected to state READ COMMITTED plainly and name the lease seam as
  the follow-up. (4, High) every `DbUpdateException` read as a version conflict — narrowed to
  `DbUpdateConcurrencyException` + a provider-aware `ITamDbErrorClassifier` for unique
  violations; FK/check/not-null now propagate. (5, High) ORDER NUMBERS from `COUNT(*)+base`
  (racy, recycled on delete) → a durable per-tenant counter bumped by one atomic UPDATE holding
  the row lock to commit. (6, Medium) MCP semantics — permission-filtered discovery, `isError`
  for every tool kind, spec JSON-RPC error objects. (7, Medium) unsupported extension filters
  silently dropped → fail loudly. (8, Medium) sessionStorage-token XSS tradeoff spelled out in
  docs/26. (9, Low) sample build swallowed TS type errors → `tsc --noEmit` gates it. (10,
  Medium) STATUS reconciled to a capability matrix. Verified: suites 196 (Tam.Tests) + 39
  (erp.Tests, incl. a new numbering test pinning sequential/unique/non-recycled numbers), the
  full wire matrix GREEN on fresh SQLite AND fresh Postgres (field-service 16, rule-builder 18,
  rules-gating 22, documents 31, plugin-on-plugin 7), `dotnet tam verify` artifacts current
  (no wire-surface change).
- **Review round 6 — three-agent audit of the arc 3-4 span (FE correctness, server-model
  correctness, design/beauty), then the fixes**: the span mostly held the bar (the reviewers
  singled out shipping the invalidation bus and deleting it for TanStack one commit later as
  the tier rule practiced, and the display cascade as a one-concept win), but surfaced two
  HIGHs, one confirmed cross-page bug, and a cluster of one-mechanism-where-two-grew debts.
  All fixed: (1) SILENT ACTION FAILURES — execute-mode row/toolbar actions never checked the
  findings envelope (the client deliberately doesn't throw on 422), so a rejection was
  invisible AND amplified (empty effects read as a system write → full-app refetch). One
  `completeAction` funnel now surfaces the error finding as a localized dismissible Alert
  (grid.action-failed for transport failures) and only a SUCCESSFUL operation invalidates.
  (2) BIND-PARAM GATE — PAGE001 validated only the bind's field side; the server silently
  ignores unknown query params, so a typo'd param would show EVERY row as the record's
  children. PAGE001 now also requires the param to be a query field or declared filter of the
  target grid's view. (3) RECORD LEAK — `?record` survived navigation and the unkeyed
  ModelPage kept the previous page's modal open over the new page's definition. One `url.ts`
  module now owns the whole query grammar (mode/page/record; navigation clears the
  page-scoped record), NavPage keys ModelPage by page, the sync effect reads the open record
  through a ref (stale-closure eslint-disable deleted), and openById clears the record param
  whenever the fetch fails or returns no row — the URL never claims a record the modal
  doesn't show (a cross-company deep link degrades cleanly; carrying the tenant in the URL is
  deferred). (4) ONE RECORD REPRESENTATION — flat sections normalize at Build() into a single
  implicit heading-less tab; the wire carries tabs only, the client renders chrome only when
  there's a choice, and the read-only detail stack (which the tabbed branch had silently
  lost) computes over all sections. (5) PANELTABS — `record.PanelTabs(slotId)` replaces the
  host-authored "checklists & invoices" roster tab: the marker expands client-side into one
  tab per contributing PLUGIN (heading from the panel's headingKey or the plugin's title),
  per-tenant activation filtering for free; verified live — the demo order record shows
  Detaljer + Besiktning with the host naming nothing. Also: record tab headings joined the
  L10N001 required set (typo'd key = build error, was silent raw-key render), duplicate tab
  ids are PAGE001, the SSE effect reads invalidate through a ref (manifest churn no longer
  tears down the EventSource mid-debounce), LookupSelect's loader keys off isFetching (stale
  options no longer pose as final under placeholderData), the no-hooks display contract sits
  on the exported registerDisplay/FieldDisplay jsdoc, and six new PageTests cover every new
  gate (tabs export incl. binds + panel markers, all PAGE001 rejections, XOR, dup ids, L10N
  tab headings). Deferred with this note: Sensitive-masked bind sources degrade to unfiltered
  at runtime (policy needs thought), the three-bind-vocabulary unification (merge when a
  fourth appears), element-mounted displays (the day an async display lands). Verified:
  suites 166+38, wire 18+22 on fresh SQLite AND Postgres, record wire re-normalized in the
  baseline (day-old shape, in-repo consumer), Playwright: WO tabs render, Back closes,
  mismatched deep link clears the URL, same-company deep link reopens with tabs, zero page
  errors.
- **Beauty arc 4a — record tabs (the docs/32 page model grows a dimension)**: a record surface
  is no longer a single stacked form + slot — its sections group into TABS, and the section
  vocabulary gained a `grid` kind alongside form and slot. `RecordSection` is now
  `(Kind: form|grid|slot, Id, Bind?)`; `RecordTab(Id, HeadingKey, Sections)` groups them;
  `RecordBuilder` gained `.Grid(id, bind => bind.Query(param, fromRecord: field))` and
  `.Tab(id, heading, configure)` (a record declares tabs OR flat sections, PAGE001). A GRID
  record section is a child listing filtered off the open record — each bind fills a grid query
  param from a detail-view field, so the child filters MECHANICALLY (docs/20) with no dedicated
  view; PAGE001 verifies the grid exists and every bound field is a detail result field, and the
  slot auto-declaration + SLOT001 orphan check now walk tab sections too (a shared
  `RecordSections` enumerator over flat+tabs). The open record is URL-ROUTED — `?record=<id>`
  riding arc-3c's `?mode=&page=` nav — so a record view is deep-linkable and Back closes it.
  ModelPage renders the record as Mantine Tabs when tabs are declared (flat otherwise), grid
  sections as a bound ViewGrid, slot sections as PluginSlot. THE ERP EXEMPLAR: the work-order
  detail is now Details (edit form) / Time / Materials — the Time and Materials tabs are
  `time.list`/`materials.list` grids bound to the record's own `number`, and the order detail
  splits into a Details tab and a "checklists & invoices" plugin-slot tab (inspect + invoicing
  panels get their own tab). Verified: unit suites 163+38 (the SLOT001/PLG007/PAGE001 checks
  caught the tab-walk gaps and now pass), wire 18+22 on fresh SQLite AND fresh Postgres, additive
  baseline + regenerated types, docs/32 record-tabs section; Playwright drove the work-order
  record — three tabs render, the Details tab shows the edit form, the bound Time/Materials grids
  filter EXACTLY to the record (API ground-truth: global 4 entries → bound queries return each
  WO's exact count), `?record=<id>` appears and Back closes the modal, zero page errors. The
  riding-along ERP idiom uniformity (OrderFindings rename, orders.cancel, per-domain ErpModel
  fragments, seed/stock deepening) is deferred to arc 4b with this note.
- **Beauty arc 3c — display cascade, URL-backed nav, record identity, string sweep**: the last
  FE structural slice, closing arc 3. (1) DISPLAY-RENDERER REGISTRY: `displayFor(field)` — the
  read-only twin of the input `rendererFor`, same cascade (renderer key → semantic type →
  DefaultDisplay). A grid cell and a record's read view are the same question ("show this
  value"), so they now share ONE registry: ViewGrid.cell delegates everything but the subtree
  company-name to `displayFor`, and ModelPage's read-only record fields render through it too
  (records get the grid's badges/money/boolean formatting for free instead of bare `String()`).
  Apps register custom displays by renderer/type key, mirroring registerRenderer. (2) URL-BACKED
  NAV: NavProvider's mode/page selection lives in the URL query (`?mode=&page=`) — bookmarkable,
  survives reload, Back/forward works (popstate listener), and query-params keep the SPA at "/"
  clear of the OIDC /callback. This is the seam arc-4's routed records hang off. (3) RECORD
  IDENTITY: ModelPage's `__rowId`-stuffed-into-the-row and scattered `row.tenantId`/`row.id`
  reads collapse into one typed `OpenRecord {row, key, actAs}` — the (id, tenant) convention is
  read ONCE in openRecord, and downstream reads named fields. (4) STRING SWEEP: the rule
  builder's hardcoded `"value"`/`"days"`/`"date"`/`"field"` placeholders and the scope-map's
  `"orders"` hint became locale keys (rules.placeholder-*, roles.placeholder-resource, sv+en) —
  no display text in code (D6), for the last FE holdouts. (5) TYPED-CLIENT DECISION: the
  generated `TypedTamClient` (samples/web/src/generated/tam.ts) is imported nowhere and stays
  that way BY DESIGN — the generic runtime (ViewGrid/OperationForm/ModelPage) is deliberately
  manifest-driven and string-keyed, so it needs no per-operation types; the typed client is the
  demonstrated manifest→types capability for HAND-WRITTEN app code and external consumers, and
  the sample app has no hand-written data code to use it. Kept (the generator + baseline-diff
  stay valuable), not wired into the generic runtime. Verified: web tsc + vite build; unit
  suites 163+38; wire 18+22 on fresh SQLite (FE-only slice — server logic unchanged, manifest
  additive on the new locale keys, so Postgres parity inherited); Playwright confirmed the
  customers grid renders through the display cascade (boolean ✓/— column, dimmed nulls) and
  URL-backed nav end to end (navigate → ?mode=work&page=customers; reload restored Kunder;
  Back returned to Ordrar), zero page errors. Arc 3 complete.
- **Beauty arc 3b — TanStack Query for the FE data layer (supersedes 3a's bus)**: the
  hand-rolled `dataVersion`/`invalidate()` bus was a stepping stone; the elegant answer for
  solved plumbing is not to hand-roll a cache (Tam's own tier rule — roll-your-own only for core
  semantics; those all live on the server). The generic data layer now runs on TanStack Query.
  `useView(viewId, params, {actAs})` is the one cached read every grid, picker and panel goes
  through — keyed by `['view', id, params, actAs, culture]`, so two surfaces on one view dedupe,
  remounts hit cache (stale-while-revalidate), and loading/error are the query's own states (the
  hand-rolled rows/total/loading useState and the fetch effect are gone; the grid.load-failed
  Alert now renders off `isError`). The manifest is just another query (`['manifest']`), so
  `refreshManifest` is `invalidateQueries(['manifest'])`. INVALIDATION IS TARGETED and unified:
  `invalidate(effects)` reads each committed `entity-modified` effect and invalidates only the
  views over that entity — the entity→view map derives from the manifest's own `extensibleEntity`
  set (no hardcoded list), and a system/config write (a non-extensible entity, or an unknown
  effect set) additionally refetches the manifest. The SAME function serves mutations (operation
  response effects) and live refresh (SSE payload effects, debounced), so a committed write and a
  cross-client event invalidate identically — the four-mechanism tangle (refreshKey / localRefresh
  / onAction / per-grid subscribeEffects) is now one effect-keyed call, and the GenericGrids
  manifest-refresh special-case is deleted (a system write names a system entity → the manifest
  refetches itself). LookupSelect rides the same cache (searches dedupe/cache). `@tanstack/react-
  query` is a real dependency in the framework runtime — accepted because it is UI-agnostic
  plumbing (orthogonal to Mantine), not pixels. Verified: web tsc + vite build; unit suites
  163+38; wire 18+22 on fresh SQLite (server binary and manifest byte-identical to 2248ab2, so
  Postgres parity is inherited from its green run — zero server delta this commit); Playwright
  drove a live create → the grid reloaded 8 rows via targeted invalidateQueries with the modal
  closing and ZERO page errors (screenshots). Remaining arc-3 items (display-renderer registry,
  URL-backed nav, ModelPage record-identity cleanup, hardcoded strings, typed-client note) follow.
- **Beauty arc 3a — the invalidation bus (FE structural pass, slice 1; SUPERSEDED by 3b)**: the review's
  headline FE tangle collapsed. Four parallel "reload this grid" mechanisms — a `refreshKey`
  prop threaded parent→grid, an internal `localRefresh` counter, an `onAction` callback bubbled
  up for manifest refresh, and a per-grid SSE `subscribeEffects` debounce — became ONE concept:
  `dataVersion` + `invalidate()` on the context (`@tam/react`). A committed write (form success,
  row action) or a committed effect over SSE (now debounced once, centrally) bumps the counter;
  every subscribed view depends on it and reloads. ViewGrid lost three props and its own SSE
  wiring; ModelPage's record-form success calls `invalidate()` instead of bumping a private key
  (so the primary grid reloads through the bus, not cross-component plumbing); the
  manifest-refresh-after-admin-write policy moved to ONE place — a `GenericGrids` wrapper that
  `useInvalidation(refreshManifest)` — so declared domain pages no longer over-refetch the
  manifest on every order create. The grid also gained a real fetch-error surface (`loadError`
  → a localized Alert, `grid.load-failed`) where a failed view load used to silently show an
  empty table. Verified: web tsc + vite build; unit suites 163+38; wire 18+22 on fresh SQLite
  AND Postgres; Playwright drove a live create — the orders grid reloaded 6→7 rows with the
  modal auto-closing, proving the bus end to end (screenshots). Remaining arc-3 items
  (display-renderer registry, URL-backed nav, ModelPage record-identity cleanup, hardcoded
  strings → locale keys, typed-client decision) are the next slice.
- **Beauty arc 2b — plugin ergonomics**: the authoring surface a vendor actually touches,
  deburred. (1) TYPED WIRE READS: new core `WireValues` — one accessor family over gate
  inputs, effect payloads and host view rows (`gate.Guid("orderId")`,
  `effect.String("number")`, `response.FirstRow()?.Decimal("estimatedTotal")`,
  `response.WireRows()`), one rule (missing/null/mismatch → null, never throws) — and the
  `TryGetProperty`/`SerializeToElement` ceremony deleted from all four sample plugins
  (fortnox's private Str helper, invoicing's three hand-rolled extract blocks, inspect's
  guard stanzas, approvals' GetProperty throws-on-missing). The names a handler passes are
  the same wire names RequiresView/RequiresEvent declared. (2) CEREMONY REMOVAL: the source
  generator now also emits `AddDiscovered(this PluginBuilder)` so plugins write
  `plugin.AddDiscovered()` (no more plugin.Model.* flip), and AddPlugin/AddPackage load
  embedded `locales/*.json` automatically — the 16 `plugin.LocaleDefaults()` lines across
  packages and samples deleted; shipping catalogs is the convention, not a Configure line.
  (3) APPROVALS DIALECT PORT: the oldest plugin rewritten in the current dialect —
  Configure is a table of contents over IPluginPart parts (AdminSurface/ReviewSurface),
  plugin.Form/Grid, and the surfaces are finally REACHABLE: declared pages
  (approvals.admin: groups grid + new rules grid off a new approvals.rules.list view;
  approvals.requests: the queue) with nav suggestions — plus the groups grid uses RowForm
  with the list deliberately carrying `groupId` so assign opens prefilled (the docs/32
  contract, second consumer). (4) REPLAY SCOPING: EnvelopeReplay is now plugin-scoped like
  the writer/reader seams — scoped service reading the PluginContext stamp, PLG012 without
  it, and the idempotency key became `replay:{plugin}:{envelope}` so release provenance is
  structural (in the stored key the audit shows), never an argument. Verified: suites
  163+38 (new PLG012 test), wire 18+22 green on fresh SQLite AND Postgres, additive
  baseline + regenerated types (42 views), docs 22/28/31 + tutorial steps 1/13/16 synced.
- **Beauty arc 2a — structural unifications**: four parallel mechanisms collapsed into single
  descriptors, and one canon finished. (1) GRID ACTIONS: RowActions/RowForms/ToolbarActions and
  contributed actions were four lists carrying one idea — now ONE `GridActionSpec(Operation,
  Placement: Row|Toolbar, Mode: Execute|Form)`; the wire shape is a single `actions` array
  (`{operation, placement, mode, bind?, plugin?}`), plugin contributions merge in as
  row/execute descriptors with their bind maps, the builder verbs stay as sugar
  (`RowAction`/`RowForm`/`ToolbarAction`), and ViewGrid renders from one filtered list instead
  of four code paths. Panels grew `headingKey`/`order` so contributed detail panels title and
  sort themselves declaratively. (2) RULE ENGINE TIER SPLIT: the evaluator lived inside
  Packages/Rules.cs — but "the service is core; the package is only the surface" (the RoleStore
  rule), so RulesGate/RuleActionsGate/RuleEvaluator moved to their own RuleEngine.cs, and the
  extraction paid for itself: the three near-identical evaluate loops (gate, transactional
  actions, dispatch actions) deduped into one `EvaluateRuleAsync` core (a `Firing` record,
  pure-pass row detach + memoization, one shared rule-fault exception list, one shared
  set-field applicator) — Packages/Rules.cs shrank 354 lines to surface-only. (3)
  `ExtensionFieldRules`: extensions.define-field and package install validated + built field
  rows separately; now one Validate (UnknownEntity/UnknownType/InvalidKey/MissingLabel with
  caller-supplied finding paths) and one Build row-factory serve both doors — the RoleRules
  pattern repeated. (4) RETIRE CANON: wherever define upserts by natural key, retire addresses
  the same key — `extensions.retire-field` now takes (entity, key) instead of a Guid (an
  INTENTIONAL baseline break, committed per the D4 procedure; no callers existed), the fields
  grid gained the retire row action, and roles — the one define-family without a retire — got
  `roles.retire` (Retired flag, consumers filter, define revives). Verified: suites 162+38,
  wire 18+22 green on fresh SQLite AND Postgres, docs 29/31/32 synced, regenerated types,
  baseline re-exported with the one documented break.
- **ResetOn promoted + rules editable in place (rule-builder round 3)**: the trigger-picker
  coordination graduated from renderer code into a FRAMEWORK primitive — `ResetOn`, the
  DependsOn twin for VALUES (docs/05): `form.Field(x => x.Condition).ResetOn(x => x.OnOperation,
  x => x.OnEvent)` discards a value authored against a sibling's old world when that sibling
  is edited. Manifest-carried (`resetOn`), one hop only (mechanical resets never cascade), so a
  MUTUAL pair is the cycle-safe declaration of "exactly one of the two" — which is exactly how
  the rules form now declares its trigger pair, and the pickers shrank to pure searchable
  selects with zero sibling knowledge. Second consumer immediately: orders.create's projectId
  ResetOn(customerId) — the previous customer's project no longer survives a customer change.
  And rules gained the missing EDIT path: `RowForm` (docs/32), the grid's fourth action shape —
  opens the operation's form PREFILLED from the row (same-named fields), paired with
  rules.list's result record now carrying the FULL definition (in-memory projection; small
  config table) since rules.define is an upsert by name. A screenshot-caught bug fixed along
  the way: the builder's enum value selects stored PascalCase enum NAMES where the wire (and
  the server's ordinal compare) uses camel values — now mapped through toWireEnum like the
  default renderer, which also made prefilled enum values display ("Akut"). Verified: edit
  contract test (list carries definition; re-define updates in place, no duplicate), suites
  162+38, wire suite grown to 18 checks (manifest resetOn/rowForms, edit round-trip) green on
  fresh SQLite AND Postgres, rulesgate 22/22 on both, additive baseline + regenerated types,
  screenshots of the dynamic form and the prefilled edit modal (relative-date clause rendered
  as "today ± 7").
- **Rule-builder round 2 — the form dogfoods its own dynamics (docs/05) + review fixes**:
  `rules.define` now uses the framework's OWN form machinery instead of renderer-local gating:
  `VisibleWhen` Px hides Condition/Messages/TargetField/Action until a trigger is chosen (and
  TargetField whenever an action is set — it anchors a finding), `RequiredWhen` flips Messages
  required exactly when RUL003 will demand it (Input.Messages became nullable — baseline-safe
  required→optional), and a server derivation (`rules.define.target-fields`) supplies
  TargetField's OPTIONS from the trigger's own localized fields via the resolve endpoint, so
  the anchor is picked, never typed. Enablers and fixes from the self-review: OperationForm
  renders field renderers as REAL component boundaries (own hook lists — hook-safe under
  VisibleWhen mounting) and accumulates ALL keys changed in a batch for the resolve trigger
  (a renderer may set several siblings at once); define REFUSES comparisons against the null
  constant (the unfinished-clause shape) with a redirect to isNull/isNotNull, and the builder
  shows an inline "value required" hint; changing the trigger resets condition/action/target
  (hidden fields still submit — stale refs must not survive); unparseable Advanced JSON locks
  the raw editor instead of round-tripping to an empty visual model; the rules.schema fetch is
  cached per (revision, trigger) and shared by the condition + action editors. Verified: 6
  definition tests (null-compare refused, isNull sanctioned, messages optional-for-action),
  full suites 162+37, wire suite grown to 12 checks (resolve dynamics: hidden→visible,
  required flip, derivation options) green on fresh SQLite AND Postgres, rulesgate 22/22 on
  both, additive baseline + regenerated types, screenshot of the dynamic form with the inline
  clause errors, the option-typed value select, and TargetField as a dropdown.
- **Rule-builder UI BUILT (docs/22) — the P5 UI slice, "the BE Form way"**: the raw Px-JSON
  textareas on `rules.define` are replaced by a visual builder — a searchable trigger picker
  (operation OR event, the two clearing each other), typed condition clauses (field → operator
  → value), and a set-field/finding/publish-event action editor — all rendered from data the
  SERVER types. The one thing the manifest can't give (a target ROW's compiled field types) is
  filled by a new computed **`rules.schema` view** (`?trigger&kind`, behind `rules.manage`):
  one row per compiled row field `{path, labelKey, wireKind, options, entityKey}` through the
  same `FieldModel` path that types operation fields, so `row.status` arrives as a string with
  its enum options and `row.budget` as a number; RUL004-shaped (no single `{entity}Id` → empty),
  pure and synchronous (extension fields come from the manifest overlay the client already has).
  The client only ASSEMBLES: `conditionRefs` unions input/payload + extension + row fields,
  `operatorsFor(wireKind)` picks operators, the value control follows wireKind/options (enum →
  localized select, number → numeric, date → specific OR relative `fn` "today ± N", boolean →
  true/false); `buildCondition`/`parseCondition` round-trip losslessly to the Px the evaluator
  runs, with a raw-JSON "Advanced" fallback for expressions that don't fit the flat clause shape.
  New renderer props `form`/`setField` let a renderer read/coordinate siblings (the trigger).
  Honest limit: event payload fields are names-only, so an event trigger's top-level fields
  default to string/equality while its `row.*` fields get full typing. Verified: 3 backend view
  tests + an 8-check wire suite on SQLite AND Postgres (schema shape, enum options, excluded
  extension bag, event target row), rulesgate regression 22/22 on both, web tsc + vite build
  clean, additive baseline + regenerated typed client, and a UI screenshot of the populated
  builder with `row.status` offering its localized `Öppen`/`Avslutat` options. **P5 is done.**
- **Effect-triggered rules BUILT (docs/22) — the last P5 rules slice, built with the user in
  the loop for the dispatcher change**: a rule triggered by a DOMAIN EVENT (`onEvent`) instead
  of an operation, evaluated on the outbox dispatch path after plugin subscribers. Condition
  reads the event payload + `row.*` (the entity the payload references by `{entity}Id`); the
  action is set-field ONLY — RUL007 forbids publish-event and RUL006 forbids a `rules.*`
  trigger, which together make a rule→event→rule cycle structurally impossible. The write uses
  the same round-5 `RejectSetFieldValue` guard and tenant-checked row load as operation rules,
  rides the dispatcher's per-record SaveChanges, and is isolated like a subscriber (a broken
  rule never wedges dispatch). `onOperation` became optional (baseline-safe: required→optional).
  Verified: 3 harness tests + 4 wire checks ("order created → project orders flagged on
  dispatch"), full 16-suite matrix 242/242 on fresh SQLite AND Postgres, additive baseline.
  **P5 rules are now complete** — conditions over input + row state, relative dates, the action
  catalog, and event triggers. (The visual rule-builder UI followed, above; remaining P-work is
  P4 custom objects.)
- **P5 rules engine status**: BUILT — Px-conditioned findings; row.* conditions over the
  operation's target row; the PxFn relative-date node; the action catalog (set-field +
  publish-event), hardened by review round 5; and effect-triggered rules (above).
- **Review round 5 (rules-engine write paths): two adversarial agents, two confirmed bugs +
  hardening**. The action catalog and row.* increments got a security + correctness audit;
  both agents independently flagged the same HIGH, and the correctness agent found a live
  evaluator bug. Fixed: (1) **set-field validation bypass** — the action write skipped every
  check the wire channel enforces (ReadOnly/plugin-owned, field state, semantic type,
  options); it now runs the SAME guard at define AND re-checks at execute (so a rule can't
  outlive a field's constraints), degrading to the non-blocking warning on failure. (2)
  **PxBinary.Truthy on a JsonElement const** — a tenant const/field in a boolean position
  silently evaluated false server-side while the client fired; Truthy now normalizes, so the
  two evaluators agree. Hardening: the `rules.` event prefix is RESERVED at build (closes the
  temporal event-collision gap — no package may declare `rules.*`); action + finding rule
  queries are deterministically ordered; the pure-phase row read DETACHES so it can't poison
  the handler's identity map; the action pass caches specs per entity; condition/action
  length is bounded at define; `PxFn.Today` is `[ThreadStatic]`; PLG002 already keyed on
  handler type. Documented as accepted residual risk: per-write rule provenance on the audit
  entry (rule definitions are themselves audited, so causation is reconstructable) and the
  client-evaluator preview drifts (the server is authoritative and re-evaluates). Explicitly
  cleared as solid by both agents: cross-tenant reach, deserialization/depth bombs (STJ
  depth-64), idempotency replay (short-circuits before gates — no double-fire), rollback
  (action writes are transaction-scoped), and the RLS interplay. Verified: 162 framework + 29
  sample tests (F1/F2 regressions incl. bare-const truthiness and options rejection), full
  16-suite matrix 238/238 on fresh SQLite AND Postgres.
- **P5 action catalog BUILT — rules that DO, not just veto (docs/22)**: a rule's action is
  validated data from a closed set. `set-field` writes a REGISTERED extension field on the
  operation's target row — executed by a second, TRANSACTIONAL rules gate so the write rides
  the operation's own SaveChanges (one commit, one audit entry; compiled state stays behind
  intents, EDIT001 extended to tenants); `publish-event` lands an outbox row with the
  derived type `rules.{name}` in the same commit (never a chosen id — RUL005 refuses
  contract collisions) and dispatches like any event, which also makes "enqueue an
  integration message" pure composition with docs/25's event triggers. Actions never block;
  finding rules stay the pure pre-transaction gate. RUL005 (`rules.invalid-action`) names
  what is wrong at define time (unknown type, unregistered set-field target). PLG002
  refined to key on handler type — one plugin may now deliberately run a pure AND a
  transactional wildcard gate. Verified: 4 action tests through the harness incl. the
  same-commit extension write and the outbox row (28 sample tests total), rules wire suite
  grown to 18 checks (set-field visible on the wire row in the same request, registry
  validation), full matrix 238/238 on fresh SQLite AND Postgres, baseline additive
  (rules.define gained the optional `action` field).
- **Px `fn` node BUILT — relative dates in rule conditions (docs/22)**: RTFM #3's sharpest
  find, closed the same day it was queued. `{"t":"fn","op":"today","days":7}` evaluates
  today's UTC date (+offset) as the ISO string dates already compare in — FRESH on every
  check, on BOTH evaluators (PortableExpressions.cs + @tam/core px.ts stay mirrors), with a
  test-only clock seam for determinism. The erp seed's urgent-schedule-window rule now uses
  it — the policy no longer drifts a day after definition. rules.define admits `fn` into
  the closed vocabulary (unknown fn ops are named in rules.invalid-condition like any
  operator). Verified: 2 deterministic clock tests (159 total), rules wire suite +2 checks
  proving the SEEDED fn rule blocks an urgent schedule 10 days out and passes 3 days out on
  any run day, full matrix 234/234 on fresh SQLite AND Postgres.
- **RTFM #3 (docs-only build over the reshaped docs): work-order priority + the urgent-
  scheduling policy** — a consumer agent shipped a real feature with zero framework edits:
  `WorkOrderPriority` enum (default carried by the domain, wire field optional so the change
  stays D4-additive), `work-orders.set-priority` as an INTENT (EDIT001 refused the
  Change<T> shape — the analyzer's opinion beat the design), grid column + filterable, and
  the tenant rule "urgent work orders can't be scheduled more than 7 days out" authored as
  data — a MIXED condition (`row.priority` from the target row, `scheduledDate` from the
  input), the exact combination row.* shipped for, proven over real HTTP. Its report drove
  same-day fixes: the docs' Px condition example was WRONG (sketch shape without the `t`
  discriminator — rejected by the engine; docs/22 + step-09 corrected, operator vocabulary
  documented, finding-code convention `rules.{name}` stated, rule seeding shape shown);
  rules.define 500'd on malformed condition JSON (now a rules.invalid-condition finding
  that also NAMES an unsupported operator; 2 regression tests); step-12's command line had
  a project-relative path bug; step-11 gained the harness return types. Queued: a relative-
  date Px node (`{"t":"fn"}`, "today"+offset) — "7 days out" currently only works as a
  define-time constant, the round's sharpest find. Verified: 157 + 24 tests, full wire
  matrix on SQLite AND Postgres, additive baseline confirmed by the agent running the
  impact tool as documented (its first outside user).
- **P5 `row.*` BUILT — rule conditions over the operation's target row (docs/22)**: the
  intent-operation blind spot closed. rules.define resolves the target entity from the
  operation's single `{entity}Id` input and stores it on the rule (RUL004 names the wall
  when there is no single target — orders.create with two id inputs is the wire-proven
  case); RUL002 now verifies `row.{member}` against the entity and `row.ext.{key}` against
  the tenant registry. At evaluation the row hydrates once per (entity, id), read-only,
  pre-transaction, explicitly tenant-checked, and is serialized through TamJson before Px
  sees it — so `row.budget` (Money) compares as a number and `row.status` (enum) as
  "open", wire-identical to input conditions. A missing row means the rule does not fire;
  only unevaluable rules warn. "Big open projects can't be closed" is now a tenant-authored
  rule, no deploy. Verified: 4 evaluator unit tests (157 total), rules wire suite grown to
  12 checks (define/block/pass/RUL004/RUL002-over-row/retire), full 16-suite matrix
  232/232 on fresh SQLite AND Postgres, manifest untouched (rules are data — the impact
  tool confirms "no manifest changes").
- **Step 12 BUILT — the consolidated change-impact report**: `TamImpact.Against(model,
  baseline)` (Tam.Core) diffs the compiled model's manifest against the committed baseline
  and prints the tutorial's unified answer: ✓ the silent greens stated per change (schemas,
  bound forms/grids, MCP, TS-client regen), ✗ the D4 breaking classification (the same rules
  the CI baseline gate enforces — shown BEFORE the push, with the tool exiting 2 so scripts
  can branch), ! the couplings the manifest knows (gatedBy plugins, event subscribers,
  integrations mapping the operation, RequiresView contracts over the changed view —
  including a contract field no longer served, which is listed per plugin AND counted as a
  break). Host CLI: `dotnet run -- impact [baseline]` via the same TamManifestExport hook as
  the export mode. Verified: 4 unit tests (unchanged/new-required/removals/additive) + the
  real erp report against a rolled-back baseline (output now IS the tutorial page). With
  this, the deferral list is EMPTY — every designed-not-built marker from the one-night
  review is either built or retired with rationale.
- **Deferral sweep (post-M6)**: `ViewGrid.tsx` split (`GridFilters.tsx` + `badges.tsx`;
  identical bundle hash, clean tsc — the "on next touch" debt paid on the M6-triage touch);
  the "wildcard-gate set caching" and "packaged-writer unification" deferrals RETIRED with
  written rationale in docs/29 (the first was already satisfied by `ActivationCache`, the
  second's invariants are all independently enforced in the writer). Remaining designed-not-
  built: Step 12 change-impact reports — now the only open item from the deferral list.
- **M6 triage: the RTFM report's framework asks, built same-day**:
  `TamTestHost.DispatchOutboxAsync()` (outbox dispatch on the test's clock — production
  claim-lease/tenant-pinning/poison semantics via the dispatcher's extracted
  `DispatchPendingAsync`; the M6 tests' hand-built dispatcher contortion is deleted),
  `FormFieldBuilder.EnumOptions("order-type")` (a plugin form offers another module's enum
  vocabulary as options through the model's new `Enums` registry — kebab wire name, no CLR
  coupling, ENUM001 at Build; adopted by inspect's template form, closing the free-text
  order-type wart), page section headings (`Grid(id, heading: "headings.key")` —
  locale-keyed, L10N001-gated, rendered by ModelPage; the templates page's two grids are
  now labeled), and the M6 doc batch: Step 11 harness API reference + plugin-activation
  testing recipe, docs/22 authoring additions, docs/31 multi-panel note, docs/32 row-action
  paragraph corrected against the code (row actions execute with same-name/row-id fallback —
  they never opened prefilled forms; typed-input operations belong on record surfaces or
  toolbars).
- **Field-service arc M6 (docs/34): Inspect v2 — checklists by order type, built DOCS-ONLY
  by RTFM agent #2 (zero framework edits, zero React)**: the P2 proof-piece plugin became a
  real feature. Tenant-defined checklist TEMPLATES keyed on order type (an opaque wire
  string — plugins never reference host CLR types), auto-instantiated onto new orders
  through a new order-created event (the host's contract grew: orderId/number/orderType;
  subscriber idempotent per order×template), per-item check/uncheck intents where the last
  check passes the checklist atomically and uncheck re-opens it, and the evolved gate
  blocks orders.complete only while a MANDATORY checklist is unpassed — non-mandatory
  never blocks. Mandatoriness is template data enforced by plugin gate CODE, deliberately
  NOT an automation rule: v1 rule conditions are input-only and orders.complete carries
  just an id (that wall is now docs/22's `row.*` design note, queued ahead of the action
  catalog). Two plugin panels on the orders record surface, template admin under
  administration, checklists page under work; seeded mandatory safety + non-mandatory
  handover templates demo both behaviors on boot. Verified independently after the agent:
  149 framework + 14 sample tests, 16-suite wire matrix **226/226 on fresh SQLite AND
  Postgres** (RLS on all four new tables), additive manifest baseline, new fieldm6 suite
  covering the whole arc on the wire including outbox-driven instantiation. The feature
  changing demo completion semantics rippled into three older suites (updated: inspectv2
  opts into mandatory, invoicing clears checklists first, nav gained the two pages) — a
  plugin gating a host operation is SUPPOSED to do that. The agent filed 9 doc gaps + 6
  frictions (docs/34 log); four doc errors fixed immediately (ITamDb namespace, outbox
  caveat in Step 11, event-payload enum serialization in docs/31, output-label row in
  docs/21). Standout gap: Tam.Testing cannot dispatch the outbox — subscriber tests need a
  hand-built dispatcher; `TamTestHost.DispatchOutboxAsync()` is the candidate fix.
- **Field-service arc M5 (docs/34): the friction triage — all nine fixes BUILT, under one
  principle: the type carries the defaults (docs/02)**. Semantic wrapper types now own
  their `[Format]`, `[LabelKey]` and NEW `[Lookup("view.id")]`; resolution is member attr →
  type attr → convention. `Tam.Money` ships ready-made (implicit decimal conversions) and
  the type-NAME sniff is deleted — erp's nine money fields are `Money`, its three reference
  wrappers (`CustomerId`/`ProjectId`/`StockItemId`) plus assignee fields declare lookups,
  and the manifest alone renders searchable pickers (labelField inferred from the lookup
  view's first string column; server-sent derivation options still win). That DELETED erp's
  two "SOMETHING must fire" options-derivations and the bespoke CustomerPicker wiring. The
  framework grew `users.lookup` (tenant directory as its own low-sensitivity atom — the M2
  actor-gap answer) plus `projects.lookup`/`stock.lookup` in the sample; LOOKUP001 verifies
  lookup targets at Build; advisory L10N005 (`TamModel.Warnings`, logged at startup) flags
  two wrapper types sharing one convention label key. Rounding out the log:
  `.ReadOnly()` display seat (time.book's computed amount renders disabled but
  derivation-targetable), `FieldConflict.Reason` distinguishes original-missing from stale
  on the wire, the resolve endpoint 400s with the expected `{"input": ...}` hint instead of
  500, `approvals.rules.retire` un-gates what define gated, page-placed slots auto-declare
  (the slot-declared-twice reading is gone), and the RLS read-set scaling measurement became
  a FIX: the policy takes a constant-size `app.tenant_path` GUC and semi-joins the tenant
  registry — 240 ms → 11.7 ms for a 200-node subtree over 20k rows (id-set arm kept as
  fallback; fingerprints distinguish `p:`/`s:`). Verified: 149 framework + 8 harness tests,
  full 15-suite matrix (210 checks) on SQLite AND Postgres, additive-only manifest baseline
  (fieldm2 now asserts the manifest lookup + wire rows from users.lookup instead of the
  deleted derivation).
- **Field-service arc M4 (docs/34): invoicing from completed work orders + the technician
  field mode**: the invoicing plugin now subscribes to work-order-completed and drafts from
  what the work actually COST — approved time + materials, read through service-mode
  declared reads over the M3 views (contract grew: RequiresView time.list/materials.list +
  RequiresEvent; the aggregates postdate the plugin). Amount math proven on the wire:
  approved 2000 + materials 178 = 2178 with a 5000 DRAFT entry excluded. Invoice gained a
  nullable WorkOrderId; the "order number" label honestly became "source document". Host:
  the "field" nav MODE (my-work/my-time over existing declared pages) — and the wire suite
  hides it via nav.override and restores it via nav.retire, the docs/30 v2 tenant story on
  a real surface. In sequence the suite also walks park → release → replay when M3's
  approval rule gates time.approve. New friction entry: approval RULES have no retire
  operation — a tenant can gate an operation forever but never un-gate it. Verified:
  14-check fieldm4 + full 15-suite matrix on SQLite AND Postgres.
- **Tam.Testing (tutorial Step 11, BUILT — the 13th framework package)**: the in-process
  pipeline harness. `TamTestHost<TDb>` runs the REAL executors — authorization, validation,
  gates, transaction, merge, audit, outbox — against a real provider with no HTTP;
  actors are (tenant, grant-set) pairs; assertions speak the envelope
  (ShouldSucceed/ShouldFailWith(code, onField)/ShouldBeDenied/ShouldConflictOn/
  ShouldPublish/typed Output<T>). `CapabilitySweep.RunAsync` executes EVERY view's declared
  capabilities — default sort, each sortable both directions, each filterable with a typed
  probe — so a view that compiles but cannot translate fails a NAMED test instead of
  500ing on its first sorted request, and every future aggregate is covered the moment it
  is declared. `samples/erp.Tests` is the consumer showcase (7 tests over the REAL erp
  model: pipeline scenarios incl. own-scope with two actors, a structural merge conflict,
  the work-order event contract, and the full sweep — all green first run) and runs in CI.
  Enabler: `ErpModel.Build()` extracted from Program.cs (the model is a value both hosts
  consume; manifest byte-identical). Tutorial Step 11 rewritten from designed-not-built
  onto the shipped API.
- **Field-service arc M3 (docs/34): TimeEntry + MaterialLine — built docs-only by an RTFM
  agent**: the arc's original intent realized — the slice was implemented by an agent
  FORBIDDEN from reading framework source (docs + samples + compiler errors only), and it
  shipped everything with zero framework changes: technician-owned time entries (paired
  atoms + [Widens], `ScopedUnless` on the booking technician), `time.approve` as an
  intent, snapshot semantics (hours×rate amount stored at booking; a material line keeps
  its entry-time unit price when the catalog price moves — the seed carries a visible 79
  vs 89 kr example), three derivations (live amount, latest-own-rate default, stock-item
  options), two read-only-record declared pages, materials deliberately WO-scoped rather
  than own-scoped (reasoning recorded in code). Its report filed 7 doc gaps and 6 DX
  frictions — the friction log grew more from one docs-only consumer than from two
  code-aware milestones, which is exactly why it ran this way. Verified independently:
  21-check fieldm3 suite including the approvals-plugin gate PARKING time.approve via a
  tenant-defined rule (wildcard gate over a domain that postdates the plugin), full
  14-suite matrix on SQLite AND Postgres, RLS on both new tables.
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

## Capability matrix

The one-night chronological "gaps" list is retired: most of it is now built, and the original
entries had drifted out of sync with the sections above. This matrix is the current honest state,
populated from those sections and the latest review round. States:

- **Built** — code exists, compiles, wired into the composed model.
- **Verified (unit)** — covered by Tam.Tests or the Tam.Testing harness.
- **Verified (SQLite)** / **Verified (Postgres)** — exercised over the wire on that engine (Postgres rows were run on both engines).
- **Wire-tested** — exercised over real HTTP or driven in the browser (engine-agnostic / FE-only).
- **Sample-only** — lives in a sample host, not framework core.
- **Designed** — written down in the docs, not built.
- **Known gap** — acknowledged shortfall or deliberate tradeoff (see the subsection below).
- **Retired** — built then removed or superseded.

### Core pipeline

| Capability | State | Verification | Notes |
|---|---|---|---|
| Execution pipeline (authz gate → structural validation from nullability/semantic types → transaction → same-tx field-level audit with inferred effects → idempotent replay → culture) | Verified (Postgres) | wire matrix, both engines | the spine every operation rides |
| Conflict-safe partial edits — `Change<T>` three-way merge, structured conflict (original/current/submitted) | Verified (Postgres) | tutorial wire behavior | keep-current / use-mine in UI |
| Idempotency — replay by key + payload-hash mismatch rejection, actor-scoped | Verified (Postgres) | wire | same key + different payload rejected |
| Views/grids — join + declared-capability sort + paging | Verified (Postgres) | wire matrix | semantic wrappers as wire primitives |
| Reactive create form — portable `VisibleWhen`/`RequiredWhen` + batched server resolve | Wire-tested | wire + client | evaluated client-side from the manifest AST; `RequiredWhen` is also re-checked authoritatively at submit (review round) so a form-bypassing caller can't omit a conditionally-required field |
| Mechanical filtering — `Filterable`, typed extension JSON predicates + sorting | Verified (Postgres) | wire, both engines | eq/range/contains by wire kind; `ext.{key}` via owned DbFunctions |
| Async reference lookup — `LookupSelect` / `[Lookup]` | Wire-tested | wire | server-side debounced search view |
| Roslyn analyzer — TAM001-009, L10N001, EDIT001 + PLG/RUL/NAV/PAGE/SLOT/DOC/LOOKUP/ENUM/REACH families | Verified (unit) | in-tree (several fired on real code) | build errors, not runtime; some plugin/runtime codes are still runtime-only (see gaps) |
| Source generator — compile-time discovery (`AddDiscovered`), typed facades, HostContract symbols | Built | manifest byte-identical proofs | no runtime assembly scanning |
| Manifest + OpenAPI 3.1 + MCP endpoints from the model, ETag/304 | Verified (Postgres) | wire | |
| Localization — L10N001 startup gate, sv/en catalogs, live switch | Verified (Postgres) | gate fired in dev | zero display text in code |
| Typed TS client generator + baseline-drift CI gate | Built | CI | |
| D4 additive baseline + `dotnet tam` regen/verify CLI | Built | CI | |
| Change-impact report — `TamImpact` | Verified (unit) | 4 tests + real erp report | |
| Framework-composed pages — `model.Page` / `ModelPage`, record tabs, display semantic | Verified (Postgres) | wire + Playwright | sample's registerPage count is zero |
| Navigation — manifest-driven nav v1 + tenant override registry (nav v2) | Verified (Postgres) | wire + Playwright | discoverability, never authorization |
| Tam.Testing harness + CapabilitySweep | Built | CI (erp.Tests) | in-process pipeline harness |

### Tenancy & security

| Capability | State | Verification | Notes |
|---|---|---|---|
| Tenant isolation — EF global query filter over every `ITenantScoped` + `SaveChanges` stamp interceptor | Verified (Postgres) | wire, both engines; two-context cache test | **corrects old gap 6**: no fixed "demo" tenant, no hand-written Where clauses |
| RLS backstop (Postgres) — `TamRls` ENABLE+FORCE + one FOR ALL policy per tenant table | Verified (Postgres) | psql probe as demoted role | **corrects old gap 6**: no-setting → 0 rows; 26 tables covered |
| Hierarchy capability cascade — per-role `cascade`, ancestor-chain walk | Verified (Postgres) | wire | grants fan out, data stays per-node |
| Hierarchy read scopes — `InSubtree` / `WithInherited`; act-as writes via `X-Tam-Tenant` | Verified (Postgres) | wire | global filter stays strict; a view widens explicitly |
| Capability model — access levels + explicit atoms; field masking (`[Sensitive]`) | Verified (Postgres) | wire | read masking removes the column; write masking rejects it |
| Paired-atom record scopes — `[Widens]`, `ScopedUnless`/`CheckOwnershipUnless`, TAM006 | Verified (Postgres) | wire (13/13, both engines) | replaces the retired `:own` suffix |
| Tenant lifecycle — create/move/rename/list | Verified (Postgres) | wire (11/11) | |
| Reserved permissions — `*` cannot self-entitle `subscriptions.manage*` | Verified (unit) | wire | |
| Reach seam — `ReachRef`, providers, `reach.search` picker | Verified (Postgres) | wire | naming a people-set on a row |
| Roles / users / policies / companies admin pages | Wire-tested | Playwright (headless) | |
| Subscriptions & seats — anchor model, seat leases | Verified (Postgres) | wire (106 tests) | |
| Secrets vault + SSRF egress guard | Wire-tested | wire | write-only secrets; public-IP-only egress |

### Auth

| Capability | State | Verification | Notes |
|---|---|---|---|
| Embedded OpenIddict — Auth Code + PKCE + refresh (humans), client credentials (machines), no password grant | Verified (Postgres) | curl + browser e2e | **corrects old gap 5**: real authn is built — the X-Demo-Role header was only the first authorization layer |
| Platform-global accounts — `tam:tenant` claim → request scope; membership-bound grants | Verified (Postgres) | wire | cross-tenant guard (no membership → no grants) |
| SPA auth client — `TamAuth`/`useTamAuth`, silent refresh, 401-retry | Wire-tested | browser | tokens in sessionStorage (tradeoff, see gaps) |
| Token hardening — refresh rotation, reuse-family revoke, revocation endpoint | Verified (unit) | wire (10 checks) | |
| Invite flow — `users.invite`, `ITamEmail`, one-shot link | Verified (unit) | wire (12 checks) | |
| `IActorProvider` seam for external IdP | Built | — | the auth swap point |

### Integrations & messaging

| Capability | State | Verification | Notes |
|---|---|---|---|
| Inbound integrations — mapping bind (INT001), idempotent runner, inbox + retry + dead-letter | Wire-tested | wire | failed-sync auto-recovery verified |
| Outbox dispatcher — event effects in-tx, claim-lease, poison dead-letter | Verified (Postgres) | wire | durable consumers only (dupe inline send removed) |
| Live refresh (SSE) — `IEffectBackplane` in-process + Postgres `LISTEN/NOTIFY` backplane | Verified (Postgres) | wire, cross-node | grid on instance B refreshes from a commit on A |
| Outbound integrations — event/schedule/manual triggers, retry+backoff+dead-letter, requeue | Wire-tested | wire | |
| Retention janitor — trims dispatched outbox / processed inbox+tasks / old runs / expired idempotency >30d | Built | wire (round 3) | **corrects old gap 7**: retention exists (audit + dead-letters kept) |
| Fortnox two-way connector | Sample-only | wire | inbound import + outbound push + scheduled poll |

### Plugins

| Capability | State | Verification | Notes |
|---|---|---|---|
| Packaging (P1) — `[TamPlugin]`, activate/deactivate as tenant data, manifest/MCP/OpenAPI filtering | Verified (Postgres) | wire | inactive → 404 pre-authorization |
| Depth (P2) — packaged fields, gates (ctor-DI classes), effect subscribers | Verified (Postgres) | wire | |
| Tenant packages (P3) — install / dryRun / uninstall | Verified (Postgres) | wire | all-or-nothing in the pipeline tx |
| Automation rules (P5) — `rules.define`, Px conditions over input + `row.*`, relative-date `fn` | Verified (Postgres) | wire | **corrects old gap 12** |
| Rule action catalog — set-field + publish-event | Verified (Postgres) | wire | **corrects old gap 12**; hardened by review round 5 |
| Effect-triggered rules (`onEvent`) | Verified (Postgres) | wire | **corrects old gap 12** |
| Rule-builder UI — visual trigger/condition/action editor | Wire-tested | screenshots + wire | **corrects old gap 12**: no longer raw Px in a textarea |
| Cross-domain plugins (docs/31) — `GridAction`, `IPackagedFieldWriter`, `RequiresView`, slots, panels, event contracts | Verified (Postgres) | wire | |
| Framework packages tier — `[TamPackage]`, 15 always-active modules | Verified (Postgres) | wire | admin surface is packages, not host code |
| Plugin-on-plugin — `DependsOn` L1 (activation) + L2 (contract, PLG010/011) | Verified (SQLite) | wire | fortnox `DependsOn` invoicing as the proof |
| Host contract artifact + generated symbols | Built | CI freshness gate | discoverability is the artifact |
| readOnly packaged fields | Verified (Postgres) | wire | plugin-owned state; wire extension writes rejected |
| P4 custom objects | Designed | docs/22 | waits on typed JSON predicates + index promotion + RLS |
| `Provides`-one-of / `Conflicts` relationship tiers; variability (docs/37) | Designed | docs | rev-3: variability = plugins at capability granularity |

### MCP

| Capability | State | Verification | Notes |
|---|---|---|---|
| MCP endpoint — initialize / tools/list / tools/call; 15 tools from the model; per-tenant + extension fields; `*_resolve` preflight; permission-filtered discovery; spec JSON-RPC errors + `isError` for every tool kind | Wire-tested | wire | agents hit the identical pipeline; discovery filtering + error semantics hardened this review round; remaining gap is transport (no resources/streaming) |

### Frontend

| Capability | State | Verification | Notes |
|---|---|---|---|
| tam-react runtime — context, renderer registry, OperationForm/ViewGrid/ModelPage/NavProvider, Mantine pack | Wire-tested | build + Playwright | |
| Data layer — TanStack Query `useView`, targeted effect-keyed invalidation | Wire-tested | build + Playwright | |
| Grid features — collapsing row-action menu, column chooser, filter toggle, wide-grid horizontal scroll | Wire-tested | screenshots | |
| Display-renderer registry — read-only twin of input renderers | Wire-tested | Playwright | |
| URL-backed nav + deep links — `?mode/page/record/tab/tenant` | Wire-tested | Playwright (6/6) | bookmarkable; back/forward drive it |
| Documents FE — tree browser, share picker, file-staging renderer | Wire-tested | Playwright | |
| Developer portal page | Wire-tested | Playwright | the contract as a running page |

### Retired

| Capability | State | Notes |
|---|---|---|
| Access policies / `:own` suffix (docs/27 Axis 2 v1) | Retired | built + wire-verified, then removed for the paired-atom ownership pattern (docs/28) — the encoding an analyzer can verify |
| Order / WorkOrder split | Retired | merged into one `Order`; the `work-orders.*` wire surface is gone (intentional D4 break) |
| FE invalidation bus (arc 3a) | Retired | one-commit stepping stone, superseded by TanStack Query (arc 3b) |

### Known gaps & deliberate tradeoffs

- **Transactional gate concurrency is READ COMMITTED**, not a frozen / repeatable read — concurrent
  writers can race a gate's precondition. A declared-lease seam for gates needing a true freeze is
  the designed follow-up (the pipeline comment now states this plainly, per the review round).
- **MCP** is minimal JSON-RPC over HTTP: no resources, no streaming. (Discovery is now
  permission-filtered and the JSON-RPC error / `isError` semantics were corrected this review round —
  see the MCP row above; the remaining gap is transport surface, not correctness.)
- **SPA tokens live in sessionStorage** — the documented no-BFF tradeoff (tab-scoped; the server
  enforces rotation/revocation). BFF mode is not offered; docs/26 now spells out the XSS blast-radius
  this trades against.
- **Per-slice contract additivity check is pending** — host + per-plugin contract *freshness* is
  gated, but additive-only checking across plugin contract slices is not.
- **Value update policies**: only `RecomputeIfUntouched`; `DefaultOnce`/`Derived`/
  `RequireConfirmation` and `SuggestFrom` bindings are absent.
- **Context views in forms** (`form.Context/Show`) are not implemented; contextual data flows via
  derivations/suggestions.
- **View result records must be init-property** — TAM007 makes positional-ctor projections (which EF
  cannot compose sort over) a build error, resolving the old ambiguity by convention, not a rewrite.
- **Plugin subscriber delivery is at-most-once** — no retry/inbox for plugin subscribers yet.
- **Field-level audit** shows the extensions column as one change, not per extension key.
- **Package uninstall** leaves package-defined roles in place (they may already be granted to users).
- **Offline parity for tenant rules** needs the rule conditions merged into the manifest (client-side
  portable evaluation) — not done. Integration **reconciliation** and **offline/mobile** are not started.
- **Custom objects (P4)** and the **`Provides`/`Conflicts`** relationship tiers are designed, not built.
- Some plugin/runtime diagnostic codes (`PLG###`/`RUL###`) are runtime errors/findings, not yet
  Roslyn analyzer diagnostics; field metadata is still reflected at startup (manifest now memoized by
  ETag), and a few designed diagnostics (L10N000, DB001, EDIT002) remain unbuilt.

## The one-night verdict

Everything architecturally risky that could be tested in one vertical slice — portable AST with
dual evaluators, manifest-driven rendering, three-way merge semantics, the extension overlay, the
localization gate, MCP-from-model — worked as designed. The costliest remaining item is the real
compiler package (source generator + analyzers + impact reports), which is exactly what the
design predicted (docs/review-notes.md).
