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
    zero-setup dev default. Extension filtering covers string-typed fields (containment over the
    canonical JSON); numeric extension filters, extension sorting, and expression-index
    promotion remain.
11. Grid row-action input mapping is a name-match heuristic; batched per-row action availability
    (review-notes risk #4) not implemented.
12. **Plugin system is designed, not built** ([docs/22-plugins.md](docs/22-plugins.md), decision
    D8, tutorial step 13): compiled namespaced plugins with per-tenant activation, packaged
    fields on host entities, operation gates, tenant packages, custom objects, Px-bounded
    automation rules. Phases P1–P5 defined; P4 (custom objects) additionally waits on typed
    JSON predicates + index promotion + RLS.

## The one-night verdict

Everything architecturally risky that could be tested in one vertical slice — portable AST with
dual evaluators, manifest-driven rendering, three-way merge semantics, the extension overlay, the
localization gate, MCP-from-model — worked as designed. The costliest remaining item is the real
compiler package (source generator + analyzers + impact reports), which is exactly what the
design predicted (docs/review-notes.md).
