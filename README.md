# Typed Application Model

A minimalistic .NET application framework that removes repetitive backend, frontend, integration, and agent-facing code — without becoming a low-code platform or replacing normal programming.

The system lets developers define business behavior once and derive all predictable boundary representations from it.

> **Write typed business behavior and read models once. Use derivations to resolve reactive interaction state. Bind operations and views to each boundary. Persist only explicit changes. Derive every mechanical representation and reject contradictions at compile time.**

**Status: working vertical implementation.** The framework packages (`src/Tam.*`), the TypeScript/React runtime (`packages/`), and a demo ERP (`samples/erp` + `apps/web`) run end to end — reactive forms, conflict-safe edits, runtime tenant custom fields, localization, and MCP included. See [STATUS.md](STATUS.md) for what's verified and the honest gap list against the design, and [docs/screenshots/](docs/screenshots/) for the app.

```bash
cd samples/erp && dotnet run     # API + web app on http://localhost:5100
dotnet test                      # framework tests
npm install && npm run dev:web   # frontend dev loop
```

## The five concepts

| Concept | Responsibility |
| --- | --- |
| **Domain State** | Normal C# entities, value objects, domain rules, EF Core persistence |
| **Operations** | The only way durable business state changes — typed input/output, authorization, transactions, validation |
| **Views** | Typed read models: details, grids, lookups, reports, integration exports, agent resources |
| **Derivations** | Computed interaction state: visibility, requiredness, options, defaults, suggestions, warnings, server validation |
| **Bindings** | Boundary exposure of operations and views: HTTP, web/admin/mobile forms and grids, MCP, integrations |

A sixth cross-cutting concern, **tenant extensibility** (runtime custom fields defined by tenant administrators), is designed as a second authoring channel into the same model — see [docs/15-extensibility.md](docs/15-extensibility.md).

## Document map

| Doc | Contents |
| --- | --- |
| [01 Overview & principles](docs/01-overview.md) | Goal, core principle, development principles, explicit non-goals |
| [02 Domain state](docs/02-domain-state.md) | Entities, value objects, semantic value types |
| [03 Operations](docs/03-operations.md) | Operation contract, what the framework derives, patch-vs-intent guidance |
| [04 Views](docs/04-views.md) | Typed read models, grids as bindings over views |
| [05 Derivations](docs/05-derivations.md) | Field state resolution, findings, portable vs contextual, dependency graph, update policies |
| [06 Bindings](docs/06-bindings.md) | Form, mobile and grid bindings; server-defined semantics, client-defined presentation |
| [07 Partial edits & conflicts](docs/07-partial-edits.md) | `Change<T>`, three-way merge, semantic equality, cross-feature forms |
| [08 Validation model](docs/08-validation.md) | Semantic / operation / database / derivation validation authority |
| [09 Envelope & effects](docs/09-envelope-and-effects.md) | Execution envelope, pipeline responsibilities, effects |
| [10 Integrations](docs/10-integrations.md) | Integration bindings, runtime infrastructure, reconciliation |
| [11 MCP & agents](docs/11-mcp-and-agents.md) | Operations as tools, views as resources, preflight resolution |
| [12 Compiler & manifest](docs/12-compiler-and-manifest.md) | Source generator, compiled manifest, static/dynamic split, diagnostics, impact reports |
| [13 Frontend runtime](docs/13-frontend-runtime.md) | TypeScript manifest and clients, generic runtime, renderers, overrides |
| [14 Database & EF Core](docs/14-database-and-ef-core.md) | EF Core integration and consistency verification |
| [15 Extensibility](docs/15-extensibility.md) | **Per-tenant custom fields**: field registry, effective manifest, storage, pipeline, guardrails |
| [16 Packages & layout](docs/16-packages-and-layout.md) | NuGet package structure, application repository layout |
| [17 Implementation phases](docs/17-implementation-phases.md) | Build order with validation targets per phase |
| [18 Success criteria](docs/18-success-criteria.md) | What developers write vs never maintain by hand |
| [19 Decisions](docs/19-decisions.md) | Decided: authorization, tenancy topology, audit, operation evolution, real-time scope |
| [20 Tutorial](docs/20-tutorial.md) | **A complete feature end to end** — every line written, everything derived |
| [21 Localization](docs/21-localization.md) | No display text in code: keys, per-culture catalogs, build-enforced coverage |
| [Review notes](docs/review-notes.md) | Design risks, refinements, and open questions |

## Reading order

Start with the [tutorial](docs/20-tutorial.md) to feel the developer experience, then 01–09 for the core model, 15 for tenant extensibility, and [review-notes](docs/review-notes.md) for the critical assessment of where the risk is. The tutorial doubles as the acceptance test for the implementation: when it can run top to bottom, the framework is real.
