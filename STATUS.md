# Implementation Status

Built overnight as a working vertical implementation of the design docs, validated end to end by
the Norrservice ERP sample. This file is the honest ledger: what runs, what's verified, and where
reality still falls short of [docs/20-tutorial.md](docs/20-tutorial.md).

## What runs

```
src/Tam.Core                 contracts, findings+args, Change<T>, semantic types, portable AST,
                             model builder, manifest, locale catalogs (L10N001 gate at startup)
src/Tam.Compiler             Roslyn analyzer: TAM001-003 model-shape checks and L10N001 locale
                             coverage as build errors (locales via AdditionalFiles)
src/Tam.EntityFrameworkCore  three-way merge, field-level audit + inferred effects, idempotency,
                             ExtensionData JSON column, tenant field registry storage
src/Tam.AspNetCore           execution pipeline, view executor, batched resolve, manifest +
                             OpenAPI + MCP endpoints, outbox dispatcher, SSE broadcaster, plugin
                             system (packages/rules/integrations/subscriptions), users, and the
                             system module (framework operations with embedded sv/en locales)
src/Tam.Auth.OpenIddict      embedded OpenIddict token server + ClaimsActorProvider (the
                             framework's own auth, behind the IActorProvider seam)
packages/tam-core            manifest types, portable AST evaluator, localization, HTTP client
packages/tam-react           context/renderers/OperationForm/ViewGrid modules + renderer
                             registry, Mantine renderer pack
samples/erp                  Customers/Projects/Orders + extension/plugin/package/rule/user
                             admin, sv+en locales, seed (users, subscription)
samples/inspect              inspection-checklists plugin (packaged field, gate, subscriber)
samples/fortnox              a plugin whose whole job is one inbound integration
apps/web                     Norrservice ERP web app (Vite + React + Mantine)
tests/Tam.Tests              82 tests: merge, extension applier, Change<T> JSON, portable AST,
                             localization, auth/entitlements, plugin build validation, schedule
                             specs, reserved permissions, SSRF egress policy
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
- **Record scopes (D1 complete)**: grants may carry `:own` (e.g. `orders.complete:own`); views
  scope declaratively (`.ScopedTo(context, permission, x => x.AssignedToActorId)`) and operations
  re-check ownership authoritatively. Verified: the technician role sees only assigned orders and
  is rejected (localized) when completing others.
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
  fields with zero new downstream code. *Gates*: `plugin.Gate("orders.complete", …)` runs
  declared preconditions after validation, before the handler — the gate reads wire input and
  the plugin's own data, never host CLR types; manifest shows `gatedBy: ["inspect"]`; verified:
  unpassed linked checklist → 422 localized finding, pass → completes. *Effect subscribers*:
  `plugin.OnEffect("order-completed", …)` runs post-commit off the outbox in its own scope
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
- **Actor-attributes design drafted** ([docs/28](docs/28-actor-attributes.md), decision-ready
  D-AA1…D-AA5): the model `where` scopes wait on — attribute values as a JSON map on the
  membership, names declared by resources (`AttributeScope`, validated at policies.define like
  everything else), grants suffixed `:where:attr=value` at actor resolution on the Axis 2 seam,
  `own`/`where` unioning as row-sets, missing attributes failing closed, and `shared` shaped as a
  future declared join-scope. Design only — nothing built until the decisions are settled.
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
- **Access policies (docs/27 Axis 2, v1 `all`|`own`)**: `AccessPolicyEntity` is a tenant-scoped named
  resource→scope map, managed by `policies.define`/`policies.list` (validated against the same
  resource catalogue as levels; unknown resource/scope → localized findings); a membership lists
  policy names (`users.define … policies`, unknown → `users.unknown-policy`), resolved in the
  membership's OWN tenant like role names. Actor resolution narrows each membership's grants by ITS
  policies before the union: `own` suffixes that membership's unsuffixed atoms for the resource with
  `:own` (role-authored `:own` kept as written; broadest scope wins across a membership's policies;
  plain union across memberships per D-A5). Enforcement is the existing `:own` machinery — no
  downstream changes. Verified on the wire: didrik (dispatcher + own-orders policy) lists 0 orders
  while mcp-agent's identical role stays unrestricted; tekla's role-authored `:own` unchanged (2);
  vera/alva unaffected (5); didrik's create still passes the gate via the suffixed grant and lands
  in the tenant; a user defined live with the policy is narrowed immediately; validation findings
  fire. 82 tests; baseline + typed client regenerated (policies.define/list).
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
