# Implementation Status

Built overnight as a working vertical implementation of the design docs, validated end to end by
the Norrservice ERP sample. This file is the honest ledger: what runs, what's verified, and where
reality still falls short of [docs/20-tutorial.md](docs/20-tutorial.md).

## What runs

```
src/Tam.Core                 contracts, findings+args, Change<T>, semantic types, portable AST,
                             model builder, manifest, locale catalogs (L10N001 gate at startup)
src/Tam.EntityFrameworkCore  three-way merge, field-level audit + inferred effects, idempotency,
                             ExtensionData JSON column, tenant field registry storage
src/Tam.AspNetCore           execution pipeline, view executor (sort/page over declared
                             capabilities), batched resolve, manifest endpoint, minimal MCP server
packages/tam-core            manifest types, portable AST evaluator, localization, HTTP client
packages/tam-react           OperationForm + ViewGrid + renderer registry, Mantine renderer pack
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

Screenshots of all of it: [docs/screenshots/](docs/screenshots/).

## Gaps vs. the design docs (deliberate, in rough priority order)

1. **No Roslyn source generator/analyzers yet** (docs/12). The model is built by explicit
   reflection at startup; FORM/VIEW/GRID/L10N001 rules run as **startup gates** (fail the boot,
   not the build). Missing entirely: L10N000 (hardcoded-text analyzer), DB001 (EF length vs
   contract), EDIT001/002, impact reports, manifest baseline check (D4).
2. **View result records are init-property, not positional** — EF cannot compose sort over
   positional-record ctor projections; the compiler phase should rewrite sort into the projection
   source so the tutorial's positional style works.
3. **Context views in forms** (`form.Context/Show`) not implemented; contextual display data flows
   via derivations/suggestions instead.
4. **Value update policies**: only `RecomputeIfUntouched`; `DefaultOnce/Derived/RequireConfirmation`
   and `SuggestFrom` bindings are absent. Conditional requiredness is enforced at resolve +
   client, not re-checked at submit.
5. **Authorization is the dev allow-all actor.** Permission catalogue exists in the manifest;
   D1's roles/grants/scopes are unimplemented.
6. **Tenancy**: envelope + stamping + per-tenant registry/overlay work, but a fixed "demo" tenant,
   no EF global filters, no RLS (D2).
7. **Idempotency**: replay works; payload-hash mismatch rejection and retention policy don't.
8. **Not started**: integrations runtime (inbox/outbox/reconciliation), effects→SSE change
   notifications (D5), OpenAPI emission, generated TS *types* (the runtime is manifest-driven
   instead — typed per-operation clients are the gap), offline/mobile, audit read views/UI.
9. **MCP**: minimal JSON-RPC over HTTP (no resources, no streaming, no per-tool schema for
   extension fields on tools/list — they validate at call time).
10. **SQLite** backs the demo (JSON column as TEXT); Postgres/JSONB + expression-index promotion
    unexercised. Extension grid columns are display-only (no JSON filter/sort translation yet).
11. Grid row-action input mapping is a name-match heuristic; batched per-row action availability
    (review-notes risk #4) not implemented.

## The one-night verdict

Everything architecturally risky that could be tested in one vertical slice — portable AST with
dual evaluators, manifest-driven rendering, three-way merge semantics, the extension overlay, the
localization gate, MCP-from-model — worked as designed. The costliest remaining item is the real
compiler package (source generator + analyzers + impact reports), which is exactly what the
design predicted (docs/review-notes.md).
