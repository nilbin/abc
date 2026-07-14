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
                             OpenAPI + MCP endpoints, outbox dispatcher, SSE broadcaster, and the
                             system module (custom fields, roles, audit view as framework
                             operations with embedded sv/en locale defaults)
packages/tam-core            manifest types, portable AST evaluator, localization, HTTP client
packages/tam-react           context/renderers/OperationForm/ViewGrid modules + renderer
                             registry, Mantine renderer pack
samples/erp                  Customers/Projects/Orders + extension admin, sv+en locales, seed
apps/web                     Norrservice ERP web app (Vite + React + Mantine)
tests/Tam.Tests              17 tests: merge, extension applier, Change<T> JSON, portable AST,
                             localization
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
  label key absent from sv.json, or an enum exposed via Change<T> (EDIT001 — state transitions
  belong to intent operations) into compiler errors (all verified with negative-test operations).
- **Authorization (D1, first layer)**: role-based actors (admin/dispatcher/viewer via X-Demo-Role),
  pipeline 403s with localized findings, actor permissions in the manifest overlay, and the UI
  hides ungranted actions (verified: viewer sees no create/complete/custom-fields surfaces).
- **Live refresh (D5)**: committed effects broadcast over `/api/events` SSE; grids subscribe and
  auto-refresh debounced (verified: subscriber received entity-modified during an edit).
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
