# Review Notes — Risks, Refinements, Open Questions

An assessment of the plan as designed. The plan is unusually coherent: the five concepts are genuinely orthogonal, the non-goals are the right ones, and the "server-defined semantics, client-defined presentation" line holds up under the hardest requirement (tenant extensibility) rather than collapsing. What follows is where the risk concentrates and where the design was refined.

## Strengths worth protecting

- **The descriptive-vs-intent operation tier** is the load-bearing wall. It is what makes patch-style editing safe, what bounds tenant extensibility, and what keeps the framework from sliding into a generic CRUD engine. Any future feature that pressures this line should be treated as suspect.
- **Findings as the universal feedback shape** (validation, derivation, conflict, warning) means every boundary — forms, MCP, integrations — gets consistent, structured feedback for free.
- **The manifest split (compiled + overlays)** anticipated tenant customization before it was designed; the extensibility chapter is an elaboration, not a retrofit.

## Refinements adopted into the docs

1. **The manifest is the real public API — version it from Phase 1.** TS generation, the frontend runtime, MCP, and the tenant overlay all consume it. Treat its schema like a wire protocol: versioned, with compatibility tests and a documented evolution policy. Every API response should carry the effective-manifest revision so clients detect skew. (Phases doc updated.)

2. **Tenancy is a Phase 1 concern.** `TenantId` is already in the envelope, but per-tenant query filtering, per-tenant manifest caching, and tenant-scoped auth must exist before Phase 5 builds on them. Retrofitting multi-tenancy is notoriously expensive. (Phases doc updated.)

3. **Extensibility moved before MCP and integrations** so both adapters are built against the effective manifest rather than the compiled manifest, avoiding a second pass. (Phases doc updated.)

4. **Portable expression AST first, C# lowering second.** (See risk #1 below; phases doc updated.)

## Top risks

### 1. The portable expression model is the highest-risk component

Constrained C#-to-AST lowering via Roslyn is where scope creep and edge-case hell live ("why doesn't my ternary/string interpolation/extension method cross-compile?").

Mitigations:
- Define the AST as the primary artifact with a small closed node set (comparisons, boolean ops, null checks, membership, arithmetic, string predicates). The C# `[PortableDerivation]` lowering is a convenience layer that either lowers *completely* or fails compilation with a diagnostic listing the offending node — never partial translation.
- Keep a permanent fallback: any rule can run server-side only. Client-side evaluation is a UX optimization, not a correctness requirement. Never block a release on evaluator parity; instead, test parity property-style (same inputs → same outputs across both evaluators).

### 2. `IQueryable`-composed views will fail at runtime on untranslatable sort/filter

The compiler can only partially verify EF translatability of dynamic sort/filter composition over computed projection members. `VIEW001` helps, but the honest fix is capability declarations: views explicitly declare sortable/filterable/searchable fields, the manifest records them, bindings can only reference declared capabilities, and `ProductModel.Testing` provides a harness that executes every declared capability against a real database provider in CI. Runtime surprises become test failures.

### 3. Three-way merge correctness details

- The merge must read current values **inside the operation transaction with a row lock** (`SELECT ... FOR UPDATE` or equivalent) — otherwise there is a TOCTOU window between conflict detection and save.
- The client-supplied `Original` is convenient but self-reported. Where an aggregate has a version column, record base-version alongside and prefer server-side history for authoritative bases where it matters. At minimum, treat `Original` as untrusted input (it is) — it can produce false merges, not privilege escalation, but auditors should know the base came from the client.
- `Change<T>` JSON needs care: "property absent" vs `{"value": null}` requires custom `System.Text.Json` converters and non-default `WhenWritingNull` handling. Solvable, but specify it early because every client generator must agree.

### 4. Derivation fan-out and N+1 on grids

Row actions (`grid.RowAction(CompleteOrder.Definition)`) imply per-row availability/authorization state. Naively evaluated, that is N derivations per page. Design batch row-state evaluation (one derivation invocation receiving the page of rows) before Phase 2 ships grids with actions.

### 5. Registry quality is a product problem, not just a schema problem

Runtime-defined fields fail in practice when admins create junk (duplicate "Serial no." / "SerialNumber" fields, wrong types). The `EXT###` diagnostics catch structural errors; consider also similarity warnings at definition time and usage statistics to support cleanup. Low engineering cost, high real-world value.

## Smaller refinements

- **Unify `Result`/`Errors` with `Finding`.** The domain example returns `Errors.AlreadyCompleted` while the pipeline speaks findings. Make error catalog entries finding *factories* (code + severity + default message) so domain failures, operation validation, and derivation output converge on one shape with one localization path.
- **`ResolvedFieldState.SuggestedValue/DerivedValue` as `object?`** is fine on the wire but shouldn't be the authoring API. Keep authoring strongly typed (`Suggest(x => x.WorkAddress, value)`) and erase to `object?` only at serialization.
- **DI in static handlers:** `OperationContext` risks becoming a service locator. Prefer generator-wired parameter injection — `Execute(Input input, OperationContext ctx, IVatValidator vat, CancellationToken ct)` — where the source generator resolves extra parameters from DI. Handlers stay testable as plain static methods with explicit dependencies.
- **Idempotency semantics need one paragraph of precision:** scope keys as `(tenant, operation, key)`, store the first outcome, replay it on retry, and reject same-key-different-payload via payload hash. Define retention.
- **Deletes are unmentioned.** Decide the stance (likely: deletion is just another intent operation, e.g. `orders.cancel` vs. rare hard `*.delete` operations with cascade policy declared) — grids need it for row actions, audit needs it for trails.
- **Offline mobile is quietly hard.** `Change<T>` + three-way merge is actually a strong foundation for offline queues, but token/permission caching and stale-manifest handling deserve an explicit later design doc; don't let mobile silently assume connectivity.
- **Localization strategy:** labels/findings/descriptions all carry text. Decide early whether messages are code-resolved client-side (codes + client catalogs) or server-rendered strings; the `Finding` shape (code **and** message) suggests both — specify precedence.
- **Naming:** `ProductModel.*` is a placeholder ("product" collides with the domain word). Candidates worth a pass: `Tam.*` (Typed Application Model). Also "Derivation" is used for both field-state resolution and validation feedback — the docs lean on context, but a glossary would help onboarding.

## Open questions (need product decisions, not research)

1. **Authorization model:** roles? permission catalogue with grants? relationship-based (owner-of-record)? The permission catalogue in the manifest implies grants; per-row authorization (visible in grids, enforced in operations) needs a decision before Phase 2.
2. **Multi-tenancy topology:** single database with `TenantId` discriminator is assumed here (and required by the JSONB extensibility design as written). Database-per-tenant would change the registry, overlay caching, and migration story materially — confirm the assumption.
3. **Audit storage:** same database (simple, transactional) vs. append-only store? Effects + audit references suggest same-DB first; fine, but decide retention/immutability requirements.
4. **Operation versioning for integrations:** external callers pin schemas. When `CreateOrder.Input` gains a required field, what happens to the Fortnox mapping written last year? Options: additive-only evolution rules enforced by the compiler, or explicit operation versions. Recommend additive-only + impact reports as the v1 policy.
5. **Real-time collaboration scope:** the conflict model is submit-time. Is presence/live-updating ("someone else is editing this order") in scope ever? If yes, effects → frontend refresh gives a natural channel; if no, say so in non-goals.
