# 19 — Architecture Decisions

Answers to the open questions raised in [review-notes.md](review-notes.md). Each records the decision, the reasoning, what it commits us to, and the signal that would justify revisiting it.

**Operating assumptions** (implied by the plan's examples — orders, technicians, Fortnox/Business Central): a multi-tenant B2B SaaS with many small-to-mid tenants, back-office web/admin users plus mobile field users, and accounting/ERP integrations. Several decisions below lean on this profile; if it's wrong, revisit them first.

---

## D1 — Authorization: compiled permission catalogue + tenant-defined roles + scoped grants

> **Extended by [27-authorization-model.md](27-authorization-model.md) and settled by
> [28-assignment-and-grouping.md](28-assignment-and-grouping.md)** (built): roles are authored as
> access levels over the same atoms, fields can be masked, and D1's ":own" scope qualifier was
> RETIRED for the paired-atom pattern (base atom own-scoped, "-all" atom widens; TAM006-enforced).
> D1's substrate — the compiled catalogue and flat grant set — is unchanged underneath.

**Decision.** Three layers, matching the framework's static/dynamic split:

1. **Permissions are compiled facts.** Every operation, view, and binding declares the permission it requires (`[Authorize("orders.create")]`). The compiler emits the full permission catalogue into the manifest and fails the build on an operation without one or a reference to a nonexistent permission.
2. **Roles are tenant runtime data.** A role is a named set of permission grants, managed through normal framework operations (like the field registry — dogfooded, audited, revisioned). The framework ships default roles; tenant admins customize them. Assignment of roles to users is likewise tenant data.
3. **Record scope is a grant qualifier, not a rule language.** A grant optionally carries a scope from a small closed set — `All` / `Team` / `Own` — and an entity declares *in code* what "own" and "team" mean via a scope provider (e.g. `Order.AssignedTechnicianId`, `Order.DepartmentId`). Views enforce scope by injected query filter; operations enforce it as a precondition before the handler runs. A grid and the operation behind its row action therefore agree by construction.

Field-level read/write permissions (already designed for extension fields in [15-extensibility.md](15-extensibility.md)) apply to compiled fields through the same overlay mechanism, enforced server-side.

**Why.** Full ReBAC/ABAC (policy engines, relationship graphs) is unwarranted for this product profile and would poison the manifest's static analyzability — you can't compile-time-verify arbitrary policy. Pure static roles are too rigid for tenant variety. This split keeps *what can be protected* compiled and verifiable, and *who gets it* runtime and self-service — the same two-channel philosophy as extensibility.

**Consequences.** Scope providers must be declared per entity in Phase 1–2 (grids need them for row actions). MCP agents inherit the actor's grants automatically — no separate agent permission model. Diagnostic: a binding exposing an operation whose permission no role in the default set grants ("unreachable operation") is a warning.

**Revisit when.** A tenant needs delegation chains, record sharing, or cross-tenant collaboration — that's the ReBAC threshold, and it should arrive as a new scope kind, not a policy engine.

---

## D2 — Multi-tenancy: single database, shared schema, TenantId discriminator, RLS as backstop

> **Extended by [26-tenancy-hierarchy-and-identity.md](26-tenancy-hierarchy-and-identity.md)**
> (built): tenants now form trees (materialized paths in the tenants registry; rows still carry
> exactly one `TenantId`), accounts are platform-global with per-tenant memberships, and the
> active node is chosen at login / act-as. D2's storage shape is unchanged underneath.

**Decision.** One PostgreSQL database, one schema. Every tenant-owned aggregate carries `TenantId`; EF Core global query filters bind it from the execution envelope; writes stamp it in the pipeline, never in handlers. Tables are either tenant-scoped or explicitly marked `[GlobalData]` — anything unmarked and missing `TenantId` is a compiler error (`DB0xx`).

As defense-in-depth, enable **PostgreSQL row-level security**: the pipeline sets `app.tenant_id` on the connection per unit of work and RLS policies filter every tenant-scoped table. Application filters remain the primary mechanism; RLS exists so a single forgotten filter or raw-SQL escape hatch cannot become a cross-tenant leak.

Do **not** build database-per-tenant routing in v1 — but don't preclude it: all tenant data access already flows through the envelope's `TenantId`, connection acquisition goes through one factory, no cross-tenant queries outside `[GlobalData]`, no business meaning in global sequence values. Moving one oversized tenant to a dedicated database later is then an infrastructure exercise, not an application rewrite.

**Why.** The plan commits to one modular monolith and one PostgreSQL database; the JSONB extensibility design assumes shared schema; and for many small/mid tenants, per-tenant databases multiply migrations, connection pools, backup policies, and the field-registry story for no benefit at that scale. RLS costs little and converts the worst bug class (tenant data leak) from "possible" to "requires two independent failures."

**Consequences.** Migrations run once per deploy. The effective-manifest cache keys on `(tenant, registry revision)`. Backup/restore of a *single* tenant needs tooling (logical export by TenantId) — accept as an operational task, not a schema driver.

**Revisit when.** A contract requires physical data isolation or data residency, or one tenant's volume degrades neighbors. The escape valve above is the plan for that day.

---

## D3 — Audit: append-only tables in the same database, written in the operation transaction

**Decision.** Two tables: `audit_entries` (one row per executed operation: operation id, actor, tenant, invocation source, correlation id, idempotency key, field-registry revision, result status, timestamp) and `audit_changes` (field-level old/new values in canonical wire form — compiled and extension fields uniformly, keyed the same way the manifest keys them). Written by the pipeline **inside the operation's transaction**; `OperationResult.AuditReference` points at the entry.

Immutability is enforced by the database, not by convention: the application role has no `UPDATE`/`DELETE` grant on audit tables, plus a guard trigger. Partition by month; retention is a per-tenant policy applied by dropping partitions after logical export.

If a future compliance requirement demands tamper-*evidence* (not just tamper-resistance), add hash-chaining per tenant or ship entries to external WORM storage **via the existing outbox** — as an export, never as the source of truth.

**Why.** Transactional atomicity is the property that matters most: an audit entry that can exist without its change (or vice versa) is worse than useless in a dispute. Same-DB append-only delivers that for free and keeps "show history for this order" a plain indexed query — which the UI will want anyway. An event store adds operational surface and pushes toward event sourcing, an explicit non-goal. Effects describe what happened; they feed audit, they are not the audit.

**Consequences.** Audit rows are read-model-queryable (entity history views become ordinary views). Old/new capture rides the change-tracking + change-set machinery from Phase 4, so audit lands with it, not after it.

**Revisit when.** Regulated-industry certification demands independent storage — the WORM export path is the answer, additive not disruptive.

---

## D4 — Operation evolution: additive-only, enforced by a manifest baseline check in CI; new intent instead of versioned endpoints

**Decision.** No version numbers on operations in v1. Instead:

- **Wire names are permanent.** Renaming a C# member requires `[WireName("...")]` preserving the original; the manifest records wire names, so refactors never break callers silently.
- **Additive changes are free**: new optional inputs, new outputs, new operations, widened option sets.
- **Adding a required input** compiles only if a server-side default exists **or** every registered integration mapping and binding has been updated — the existing `INT001`-class diagnostics plus the impact report make this a build break, not a production incident.
- **Removal/repurposing is prohibited** on published operations. A genuinely incompatible change means a **new operation with a new intent name** (`orders.create-with-contract`, not `orders.create-v2`), with the old one marked `[Deprecated(sunset: ...)]` — deprecation flows into OpenAPI, TS clients, MCP schemas, and integration diagnostics automatically.
- **Enforcement is mechanical**: CI diffs the emitted manifest against the last released baseline (same pattern as .NET API-baseline checks). Any non-additive delta fails the build unless the baseline is explicitly re-approved in review.

**Why.** Versioned endpoints double every derived artifact (schemas, clients, MCP tools, forms) and rot into "v1 forever." Operations are cheap by design — a new intent name is the framework-native escape hatch, and it reads better than v2: it says *why* it's different. The manifest already exists; making it the compatibility contract costs one CI step and gives integrations the guarantee they actually need: what worked yesterday works tomorrow.

**Consequences.** The manifest baseline file lives in the repo and is updated deliberately (reviewed like a public API change). External partners can be pointed at the manifest/OpenAPI diff as a changelog.

**Revisit when.** A paid external API with contractual SLAs needs long-lived parallel majors — then introduce explicit versioning *at the HTTP binding layer only*, never in the operation model.

---

## D5 — Real-time collaboration: out of scope; ship change-notification + stale-form signaling instead

**Decision.** Presence, live co-editing, field locking, and CRDT-style sync are **non-goals** (added to [01-overview.md](01-overview.md)). What v1 ships instead, cheaply, on machinery that already exists:

1. **Entity-change notifications**: the effects pipeline ([09-envelope-and-effects.md](09-envelope-and-effects.md)) already knows what changed; publish per-entity change events over one SSE/SignalR channel, tenant- and permission-filtered.
2. **Stale-form signaling**: an open form subscribed to its record shows "this order was changed by Anna (orders.edit-details) — review changes" with a refresh affordance that rebases untouched fields automatically (the three-way merge logic, run client-side against the fresh snapshot).
3. **Grid/cache invalidation** from the same channel — no polling.

**Why.** The conflict model already makes concurrent editing *safe*; the collision rate in back-office field-service work makes live cursors a luxury. Real-time co-editing is a product in itself (operational transforms/CRDTs, presence infrastructure) and would violate the minimalism the non-goals defend. Notification-plus-merge delivers ~90% of the perceived value — "I'm not editing blind" — at ~5% of the cost, and every piece of it (effects, findings, merge) is already on the roadmap.

**Consequences.** One realtime transport enters the stack (SSE preferred over WebSockets for simplicity; SignalR acceptable if mobile needs it). Effects gain a `Notify` fan-out consumer in Phase 4–5. Nothing in the form runtime assumes liveness — offline/mobile still works.

**Revisit when.** A concrete workflow shows sustained same-record contention (e.g. dispatcher teams hammering one planning board). Even then, the answer is likely per-feature (a purpose-built board view) rather than generic co-editing.

---

## D6 — Localization: no display text in code, ever

**Decision.** All human-readable text — labels, finding messages, descriptions, enum display names — is authored in per-culture resource files and referenced from code by key; an analyzer (`L10N000`) makes a hardcoded display string a build error. Keys are derived by convention (author-once: entity members and semantic types own their label keys; inputs, columns, and forms inherit them). Finding factories carry a stable code plus structured args; the code is the message key. Catalogs compile into the manifest per culture: the server resolves messages in the request culture for headless callers (agents, integrations), and clients re-render locally from code + args — required for offline and client-side portable derivations. Default-culture coverage is a build gate (`L10N001`); other cultures produce CI completeness reports (`L10N002`). English is not special — the default culture is configuration. Full design: [21-localization.md](21-localization.md).

**Why.** Retrofitting localization is the classic framework regret: it changes the shape of `Finding`, the label model, and every generated schema. Deciding it before Phase 1 costs a design doc; deciding it after Phase 3 costs a rewrite. The multi-culture requirement is real from day one for a Nordic-market product.

**Consequences.** `Finding` carries `Args` and a boundary-resolved `Message`. `Tam.Compiler` gains catalog merging and the `L10N###` rules; the registry gains `EXT006`. Locale files are reviewable product surface in every application repository.

**Revisit when.** Never, realistically — the rule only gets more valuable as surfaces multiply.

---

## D7 — Filtering is declared, not implemented per view

**Decision.** `Capabilities.Filterable(field)` is the *single* authored fact for standard filtering: the framework composes typed equality predicates over the view's result projection (EF pushes them into SQL through the projection), grids render filter controls from the same declaration, and tenant extension fields filter mechanically via `ext.{key}` parameters. Query records carry only authored query logic the framework cannot derive — free-text search, cross-entity predicates — and shrink accordingly.

**Why.** The previous shape authored one decision twice: the capability declaration *and* a Query-record member plus hand-written `Where`. Worse, it structurally excluded tenant custom fields from filtering, since runtime-defined fields cannot appear in a compiled record. Mechanical read-side filtering over an authored projection, bounded by declared capabilities, is not the "generic CRUD" the non-goals forbid — the projection and the capability list are still the developer's authored decisions.

**Consequences.** Standard filters cost one declaration, and the declaration now yields every operator the field's type supports: equality everywhere, inclusive `from`/`to` ranges for dates, numbers and ordinal strings, `contains` for strings (see the operator table in [04-views.md](04-views.md)). Grid controls derive from the same wire kinds the server derives operators from. Anything richer extends the portable Px AST — never a string-parsed expression DSL; user input must only ever become constants, not expression structure. Extension-field filtering currently matches string-typed fields via canonical-JSON containment; promoted expression indexes remain the performance path, and numeric extension filters await real JSON translation.

**Revisit when.** A view needs a filter the mechanism can't express — which is precisely what the Query record remains for.

---

## D8 — Packaged extensibility: compiled plugins + declarative tenant channel, never tenant code

**Decision.** Extensibility beyond single custom fields comes in two packaged forms with an explicit trust boundary between them ([22-plugins.md](22-plugins.md)):

1. **Plugins are compiled, namespaced modules** (`[TamPlugin("inspect")]`) built by the product team or vetted partners, bound at host build time, and **activated per tenant as runtime data**. They contribute entities, operations, views, bindings, packaged extension fields on host entities, gates (typed preconditions) on host operations, effect subscribers, locales, and permissions — all through the same public seams the host uses, all namespace-prefixed (`PLG###` diagnostics), all D4-baseline-checked. The effective manifest omits inactive plugins' contributions entirely.
2. **Everything a tenant authors is data, never code**: tenant packages (bundled fields/roles/rules installed atomically with registry validation and dry-run), later custom objects (registry-defined entities with generated standard operations flowing through the real pipeline), and automation rules (Px-AST conditions + a closed action catalog). No tenant-uploaded scripts, no sandbox, no per-tenant compilation.

**Why.** The Salesforce comparison decomposes into pillars, and each pillar has a cheap seat on machinery Tam already has: managed packages → assemblies + the source generator + manifest overlay; custom fields at scale → the registry serialized; custom objects → the JSONB channel generalized to a document; validation rules/flows → the Px AST plus effects. The one pillar that does not transfer cheaply is Apex, and cloning it would cost the properties the framework exists for — static analyzability, the L10N/D4 gates, a manifest you can trust. The graduation path already answers "the tenant outgrew declarative."

**Consequences.** The extension overlay gains origins (compiled / plugin / tenant) with key prefixes; the pipeline gains a gate stage and per-tenant activation checks; the registry compiler grows package/object/rule validation (`RUL###`). Custom objects wait on typed JSON predicates, index promotion, and D2's RLS.

**Revisit when.** A partner ecosystem genuinely needs to ship logic without a host redeploy — the first answer is external apps over the integration channel (scoped actors + API + outbox webhooks, the marketplace's third tier); the researched escalation for hot-path in-process logic is a capability-based WebAssembly sandbox (fuel-metered, data-in/data-out, host functions only — see the trust-boundary section of [22-plugins.md](22-plugins.md)). Never raw tenant assemblies in-process: modern .NET has no isolation for them.
