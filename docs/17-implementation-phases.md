# 17 — Initial Implementation Order

> Change from the original plan: **tenant extensibility is inserted as Phase 5**, after forms and partial edits (its prerequisites) and *before* MCP and integrations — so those adapters are built against the effective manifest from day one instead of being retrofitted for tenant fields later. The manifest itself is treated as a first-class, versioned artifact starting in Phase 1 (see [review-notes.md](review-notes.md)).

## Phase 1: Core operations and HTTP

Implement:

- `Operation<TInput,TOutput>`
- Operation discovery
- Execution envelope
- Structured findings
- ASP.NET Core endpoint mapping
- OpenAPI
- TypeScript client generation
- Authorization
- Transaction pipeline
- Basic tests
- **Manifest v1 emitted and versioned** (operations + schemas), consumed by the TS generator
- **Tenancy in the envelope end-to-end** (TenantId scoping, per-tenant auth) — retrofitting multi-tenancy later is far more expensive than carrying it from the start

Validate with:

- Create customer
- Create order
- Complete order

## Phase 2: Views and grids

Implement:

- `View<TQuery,TResult>`
- Paging
- Sorting
- Filtering
- Search
- Grid manifest
- React grid renderer
- Row and toolbar actions
- Sortable/filterable capability declarations per view field

Validate with:

- Customer list
- Order list
- Invoice list

## Phase 3: Forms and derivations

Implement:

- Form bindings
- Field state
- Dependency graph
- **Portable expression AST + server/client evaluators** (AST first; C# lowering is a convenience on top — see review notes)
- Portable derivations
- Server derivations
- Batched evaluation
- Findings
- Defaults and suggestions
- Reset policies
- Revision and stale response handling

Validate with:

- Order creation
- Customer-dependent project selection
- Address defaults
- Conditional requiredness
- Server-side customer validation

## Phase 4: Partial edits and conflicts

Implement:

- `Change<T>`
- Dirty tracking
- Original/current/submitted comparison
- Three-way merge
- Semantic equality
- Structured field conflicts
- Conflict UI metadata
- Audit integration

Validate with:

- Two users editing separate fields
- Two users editing the same field
- Nullable field clearing
- Atomic address changes

## Phase 5: Tenant extensibility ([15-extensibility.md](15-extensibility.md))

Implement:

- Tenant field registry with lifecycle states and registry-time `EXT###` diagnostics
- Closed semantic type set for runtime fields (reusing compiled implementations)
- `ExtensionData` container + JSONB persistence + EF value converter and query translation
- Effective manifest: overlay merge, revisioning, caching
- Extension change sets in the operation pipeline (validation, same-transaction apply, audit)
- Extension fields in views/grids/forms via `Extensions()` opt-ins
- Per-field permissions
- Compiled-field customization overlay (labels, hiding, ordering, option sets)

Validate with:

- Admin defines "Machine serial number" on Orders; it appears in web create form, admin grid, and export with no deploy
- Conditional requiredness of a tenant field via portable rule
- Two users concurrently editing compiled + extension fields on the same order
- Field deprecation and retirement with in-flight forms
- Permission-restricted tenant field stripped server-side

## Phase 6: MCP adapter

Implement:

- Operation tools
- View resources
- Derivation preflight
- Partial-input resolution
- Elicitation metadata
- Structured findings
- **Per-tenant schemas from the effective manifest** (extension fields included)

Validate with:

- Agent creates order
- Agent resolves required project
- Agent handles validation warnings
- Agent edits only selected fields
- Agent reads and writes a tenant-defined field with elicitation

## Phase 7: Integrations

Implement:

- External mapping definitions
- Mapping diagnostics
- Inbox
- Outbox
- Idempotency
- Retries
- Dead letters
- Replay
- External identities
- Reconciliation
- Extension field mappings with configuration-time validation

Validate with:

- Customer import
- Order import
- Invoice export
- Failed synchronization recovery

## Phase 8: Compiler intelligence

Implement:

- EF model consistency checks
- Derivation cycle detection
- Required-hidden field checks
- Missing integration mappings
- Impact reports
- Permission catalogue
- Binding compatibility checks
- Frontend capability negotiation
- Shared rule host so `FORM/VIEW/INT` rule implementations also run as registry-time `EXT` checks
