# 40 — Operations own the input contract

**Status: BUILT.** An operation owns
its canonical input semantics — structural validation, derivations, and conditional requiredness.
Forms are *projections* of that contract: they choose fields, lay them out, pick renderers, and may
*tighten* validation, but they never reconstruct operation semantics and never weaken them. This doc
records the model and how it landed, closing the round-1/round-2 thrash on conditional requiredness
(Sol reviews, Finding 2).

```
Value/property metadata            (wire type, label, max length, unconditional required)
        ↓
Operation input contract           (structural validation + owned derivations)
        ↓
Selected form overrides            (presentation + optional tightening) — only when a form is named
        ↓
Effective submission contract
```

## The rule

> Define canonical input semantics **once on the operation**. Resolve them for interactive clients,
> enforce their authoritative parts on submit, and apply only the **explicitly selected** form's
> overrides.

Two failures this replaces:

- **Round 1** made a form's `RequiredWhen` authoritative at submit by scanning *every* form bound to
  the operation and unioning their rules. One presentation binding could then tighten every other
  caller's contract — including direct, MCP and integration callers who never touch that form.
- **The original** treated `RequiredWhen` as advisory-only (resolve/client), so a caller bypassing
  the form could omit a conditionally-required field entirely.

The resolution: requiredness that must be enforced is the **operation's**, authoritative for every
caller; a form's `RequiredWhen` is presentation tightening that applies only to submissions made
through that form.

## Operations own derivations (BUILT — Phase 1)

Derivations were discovered by input-type identity (`DerivationsFor(InputType)`), which let two
operations sharing an `Input` silently inherit each other's derivations. Each derivation is now
resolved to the **operation** that owns it, validated at build:

- **DER001** the input type matches no operation (orphan).
- **DER002** several operations share the input type — the derivation must name its owner via
  `[ServerDerivation(Operation = "...")]`.
- **DER003** a named owner is unknown, or its `Input` doesn't match.
- **DER004** a `DependsOn` member isn't an input field of the owner.
- **DER005** duplicate derivation id within one operation.

Resolve and manifest generation look derivations up by operation id (`DerivationsForOperation`),
never by input type.

## Forms are projections (BUILT — Phase 2)

`OperationExecutor` no longer scans every form for `RequiredWhen`. A form's requiredness is
**tightening**, applied only when a submission is made *through that named form*:

- The single operation endpoint `POST /api/operations/{id}` takes an optional `?form=` binding.
  There is **no** second submit door — MCP and integrations stay on the plain operation contract.
- `ExecuteAsync(operationId, body, context, ct, formId?)`: with a `formId`, only that form's
  `RequiredWhen` predicates apply on top of the operation contract; without one, the operation
  contract governs alone.
- A form may **tighten** (optional → required, a narrower rule) but never **weaken** an operation
  rule. Two genuinely different business contracts are two operations (or an operation mode), not a
  form that quietly relaxes validation.

## Derivation outputs carry their own authority (BUILT — Phase 3)

Authority is a property of the **output**, not the derivation method — the split that dissolves the
thrash. A derivation is *"computes operation-input state and admissibility from the current input and
context"*, and one `DerivationResult` carries all of it:

| Output | At resolve | At submit |
|---|---|---|
| `Require(field, when, finding)` | shows the required indicator | **BLOCKS** if empty, with the domain finding |
| blocking findings (`AddFieldError`, `From`, `Add` with an error) | shown on the field / globally | **BLOCK** — for every caller |
| `Lookup(field, view, filters, invalid)` — membership | opens the candidate View scoped by the filters | **enforced** by an Exists (see below) |
| `RequireOneOf(field, options, invalid)` — closed inline set | offered as the complete legal set | **enforced** — value must be one of them |
| `AddOptions`, `Suggest`, non-blocking warnings, option ordering, lookup search/sort/page | consumed | **ignored** (advisory) |

`RunDerivationsAsync` is the ONE place resolve and submit evaluate an operation's derivations, so the
requiredness a form shows and the requiredness submit enforces can never disagree. `Require()`
preserves the domain-specific finding (e.g. `orders.project-required`), so callers get the precise
message, not a generic `validation.required`.

Note the two requiredness mechanisms and when to reach for each:

- **`Require()` in a derivation** — for requiredness that depends on runtime state or arbitrary C#
  (the common case). The indicator reaches the client via `resolve`.
- A **static portable predicate** on the operation field (a future `operation.Field(x).RequiredWhen`,
  §2 of the plan) — for a rule expressible as a manifest AST, giving instant client-side requiredness
  without a resolve round-trip. Not yet built; the sample's `project-required` uses `Require()`
  because the project flow already resolves (to load the project options) the moment it applies.

## Sample migration (BUILT — Phase 5a)

`orders.create`'s "a project order needs a project" rule moved off the create form onto the
operation: `CreateOrderDerivations.AvailableProjects` emits
`Require(ProjectId, when: OrderType == Project, OrderFindings.ProjectRequired)`. Every caller is bound
by it; the form derives its indicator from `resolve`; the handler's project check stays as defence in
depth. `rules.define`'s `messages` `RequiredWhen` stays on its form as genuine presentation
tightening — a direct call falls to the operation's own domain rule (RUL003, which is richer: it
wants the default culture).

## Candidate sets are Views, membership is authoritative (BUILT — Phase 4)

Large candidate sets are not materialized inside derivations; they are ordinary Views (pagination,
filtering, permissions, tenant isolation, MCP exposure — for free, and reusable). A derivation binds
an operation field to a View plus a contextual base query — and the base query is expressed as the
view's **existing `Filterable` fields**, not a bespoke query parameter, so one mechanism serves both
browsing and the authoritative constraint:

```csharp
return DerivationResult.Empty.Lookup(
    field:   x => x.ProjectId,
    view:    "projects.lookup",
    filters: new() { ["customerId"] = input.CustomerId.Value.ToString() },
    invalid: OrderFindings.ProjectNotAvailable);
```

The base filters define the **authoritative candidate universe** (open projects of *this* customer).
The user browses it with transient parameters (`search`, `sort`, `page`) that do **not** define
validity. On submit, a selected key is validated by **existence against the base filters** — never by
whether it appeared on the last loaded page, matched the last search, or is cached in the client. The
authority comes from the derivation supplying the base filters; the front end's browsing params are
ignored. This is a real authorization boundary: the rendered subset is not proof of validity.

The mechanism (server core only — no FE change required, since the existing `[Lookup]` picker already
renders the view; exposing the constraint field lets the picker be scoped too):

- The constraint is an ordinary **filterable result field**: `projects.lookup` now projects
  `CustomerId` and declares it `Filterable`. Before, `customerId` was neither — the picker returned
  every customer's open projects; scoping it closed that latent gap.
- The selectable key defaults to the view's `id` field (a `.Key(...)` override is a future affordance,
  not yet needed).
- `ViewExecutor.ContainsAsync(viewId, baseFilters, key, context, ct)` — an efficient `Exists` that
  reuses the **same** view definition, permission checks (fail-closed: a caller who cannot read the
  candidate view cannot pass membership), tenant scope, plugin activation, SubtreeRead widening and
  the existing `BindFilters` path, then adds the key predicate. Base-filter keys are validated against
  the view's Query + Filterable fields, **type-aware** (a `.contains` on a Guid or a `.from` on a
  non-range field is not a legal key), and fail closed on anything else — neither an unknown key nor
  an operator the binder would silently drop can widen the universe. One `FilterKeys` descriptor
  drives both this check and the read path's unknown-parameter guard, so a key can't be declared
  legal and then ignored by the binder.
- **At most one lookup binding per field** (DER008): resolve surfaces one candidate universe, so two
  active lookups on a field — resolve showing one while submit enforces both — is refused at model
  evaluation, not silently mis-shown.
- `DerivationResult.Lookup(...)` is recorded as a binding; submit runs `ContainsAsync` for each
  non-null lookup-bound field (the value read from the deserialized input, so a `Change<T>` edit field
  unwraps correctly) and blocks with the `invalid` finding when the value is outside the universe. The
  same `RunDerivationsAsync` — ALL derivations, every call — feeds resolve and submit, so they cannot
  drift. Resolve surfaces the binding as a `ResolvedLookup` (view + base filters) the picker scopes
  itself by, so the client browses exactly the candidate universe rather than materializing it.
- The small-set twin is `RequireOneOf(field, options, invalid)` — authoritative closed inline options,
  enforced at submit by comparing the submitted value and each option **through the field's semantic
  model** (so `1`/`1.0`, an enum's wire token vs CLR value, and equivalent date forms compare
  correctly, and a case-sensitive code is not accepted in the wrong casing). `AddOptions(...)` (no
  finding) stays advisory: offered at resolve, never enforced.

## The authority boundary: pipeline-rejects, not commit-stable

Authoritative derivations and lookup membership run **before the transaction opens**. "Authoritative"
here means *the operation pipeline rejects this request when it is evaluated* — NOT *the underlying
database fact cannot change between validation and commit*. A project could be closed, or a candidate
row deleted, in the window between the membership `Exists` and the handler's commit. That is by design
and must stay explicit: race-sensitive invariants still need domain/handler revalidation, concurrency
tokens (`IVersioned`), locks/leases, or database constraints — the derivation is a fast, honest
front-door check, not the last line of defence. The create-order sample keeps its handler-side
project check for exactly this reason; the derivation does not replace it.

Derivations are **read-only against the ambient TAM DbContext** (DER007), structurally enforced. While
the operation evaluates them, `TenantScope.DerivationReadOnly` is set and the DbContext's write-guard
interceptor **allows only proven-read-only commands** — every `SaveChanges`, `ExecuteUpdate`/
`ExecuteDelete`, raw write, CTE-that-writes, comment-prefixed write, multi-statement write, CALL/EXEC/
DDL is rejected; the classifier fails **closed** (allow-list of SELECT/VALUES after stripping comments
and any leading CTE), not on a bypassable write-verb denylist. The guard lives inside the shared
`RunDerivationsAsync` (so resolve is covered), and its `finally` discards any tracked-but-unsaved
mutation and restores the flag on every exit path — so a write attempt fails whether the derivation
returns, blocks, or throws. The RLS backstop's session `SET` is a raw ADO command that bypasses the
interceptor pipeline, so nothing framework-owned needs whitelisting. **Scope of the guarantee:** it
covers the ambient TAM context a derivation reads through; a derivation that resolves a *different*
service (an external API client, its own new DI scope) is outside it — the boundary is "the operation's
DbContext is read-only during derivations," and a derivation computes admissibility, it does not write.
This is the right proportion for *trusted application code* (Sol re-review round 5): the framework
rejects `SaveChanges`, rejects ordinary EF write commands, and detects/clears tracked mutations — an
**accidental-misuse boundary**, defence in depth, not a sandbox that makes intentionally-evasive code
mathematically incapable of an external side effect. A provider-enforced read-only transaction or a
separate read context is the layer to add *if* derivations ever become a less-trusted extension
surface; it is not needed for the trusted-code model.

## The generated form submits through its binding

The server treats a supplied `?form=` as part of the effective contract *and* idempotency identity,
but that only bites if the caller sends it. The framework's `OperationForm` submits
`client.operation(operationId, body, { form })`, which appends `?form=`, so form-specific tightening
is actually applied in the generated UI — not merely reachable in principle. `OperationForm` also does
a **full initial resolve on mount** (gated on the manifest's `hasServerDerivations`), so a prefilled
form shows operation-derived requiredness, lookup descriptors and findings before any field is
touched, and a context-only derivation (no field dependencies, unreachable through the change-triggered
path) is resolved too. Three consistency rules keep resolve and submit under the *same* contract:

- **Same acting node.** `resolve` (initial and reactive) and the lookup picker send the form's
  `actAs` (as `X-Tam-Tenant`), exactly as submit does — a form opened from a parent subtree grid
  against a child company derives requiredness/suggestions/candidates in the child tenant, not the
  parent's.
- **Complete `Change<T>` state, one builder, both paths.** Resolve and submit send the *same* input,
  shaped by one `buildFormInput` (Sol re-review round 8): every *initialized* change-set field — own
  and extension — carries its complete `{original, value}` object. `original` is the frozen baseline;
  an untouched field arrives as `original == value`. There is no sparse-vs-complete divergence to
  drift, and a `Change<T>` field never arrives as a raw scalar (which would deserialize to
  `invalid-input`). **The actual patch is derived, not transmitted:** `TamMerge` treats
  `original == value` as a no-op — no write, and crucially *no concurrency check*, so a concurrent
  writer's change to a field the user never touched does not surface as a conflict. Partial persistence
  and field-level concurrency fall out of the merge over the three values (`Original`, `Value`,
  `Current`), not out of a sparse payload. **`WasChanged` is likewise derived:**
  `DerivationContext.WasChanged(field)` is `Original != Value` (semantic), read from the input itself —
  so it means the identical thing at resolve and submit, needs no wire-sent list, and returns `false`
  for a non-change field (not a `Change<T>` — read its value instead). Wrapper *presence* is still not
  the signal: an untouched initialized `Change<T>` has `Original == Value`. **Portable form predicates**
  (`VisibleWhen`/`RequiredWhen`) read through the one effective-value accessor that unwraps
  `Change<T>.Value`, so an edit field's predicate evaluates its value — resolve and submit evaluate the
  identical scalar, and a `RequiredWhen` may safely reference a change-set field (the complete input
  carries its value at submit). **Validation follows the same effective-patch rule** (Sol re-review
  round 9, F3): structural validation skips a change field whose `Original == Value`, exactly as the
  merge and the extension channel do — so an unchanged historical value a later, stricter rule would now
  reject cannot block an *unrelated* partial edit; only a genuinely patched field's submitted value is
  validated. **A null `Original` is a valid merge base**, not a mistake (round 9, F4): the field simply
  loaded null. The former "original-missing" conflict reason is removed — JSON cannot distinguish an
  explicit `{"original": null}` from an omitted property once both are CLR null, so a mismatch is an
  ordinary `stale` conflict. A conflict override likewise carries the persisted current value *even when
  it is null* (testing the override's presence, not its truthiness), so a "use mine" retry against a
  null current sends `original: null` rather than resurrecting the stale baseline.
- **Frozen concurrency baseline.** A `Change<T>`'s `original` is the value frozen when the form's
  `instanceKey` (its record identity) last changed, never the latest `initialValues` prop — a
  background refresh handing the same record a newer server value cannot silently rebase the
  concurrency baseline under an in-flight edit (which would let a stale edit overwrite a concurrent
  update without the intended field conflict). To adopt fresh server data as the baseline, the caller
  changes the identity, e.g. `instanceKey={`${id}:${version}`}`.
- **Same lifecycle, edits preserved.** The form resets edit state and re-resolves the pristine
  baseline only on a new record identity (`instanceKey`). A change of acting node or manifest revision
  re-resolves the **current edit state** (`currentInput()`), not the baseline — so a background manifest
  refresh or an `actAs` switch mid-edit refreshes derived state *without discarding unsubmitted edits*
  (Sol re-review round 6, F1). That context refresh first **cancels any in-flight reactive resolve**
  and is **identity-aware** (Sol re-review round 7, F2): a stale, old-context debounced resolve can no
  longer fire and win after a tenant switch (restoring the previous tenant's candidates/requiredness).
  The hard invariant (round 9, F1): an **identity change is ALWAYS owned by the reset + baseline
  effects** — the context-refresh effect resolves the current edit state *only* when the identity is
  unchanged (a same-record context change, or the form gaining its first derivation for that record).
  This holds even when record identity *and* derivation-activation land in the same render — otherwise
  the current-edit resolve would read the previous record's not-yet-rendered values under the freshly
  frozen baseline (a mixed-record request that also wins the seq race). The corollary: new prefill
  values for the *same* `instanceKey` are ignored until the identity changes — the frozen baseline and
  the user's edits win. Derived state never bleeds across records, and a refresh never reverts an edit.
- **Edit suggestions are surfaced, not silently written.** A server `Suggest()` is auto-adopted into
  the value only for a **create** (non-change-set) field. For an **edit** change-set field the
  suggestion is exposed to the renderer (as `suggestion`) with a generic accept affordance beside the
  current value (Sol re-review round 7, F4 + round 8): the user adopts it through the ordinary set path,
  which marks the field touched — so the suggestion becomes `Original != Value`, and display, merge and
  `WasChanged` all agree. Auto-writing it silently would leave `Original == Value` (a no-op the user
  never confirmed). Auto-adopting a *create* suggestion pushes the field onto the reactive-resolve queue
  so any dependent derivation recomputes (round 8), rather than displaying values and derived state from
  different snapshots.

Because submit now sends complete `Change<T>` state, a form's `RequiredWhen` **may** reference a
change-set field — the round-6 build gate that forbade it (submit was sparse then) is removed, and the
`FORM001` number returns to its originally-designed meaning (docs/12: a hidden-but-required
contradiction / dependency-cycle check). The predicate reads the field's value at submit exactly as at
resolve. **A derivation still is not automatically given the persisted aggregate**
(Sol re-review round 7, F5 / round 8): the input carries the complete *form-projected proposed* state —
enough for cross-field validation, conditional requiredness, candidate binding, findings and
suggestions — but not fields absent from the form, nor the authoritative *current* database truth. A
rule that depends on current truth (a value that may have moved since the form loaded, an unmodelled
sibling field, ownership, inventory/balances, the final domain invariant) must **load the aggregate by
the operation's identity and overlay the submitted patch**, then evaluate against that. The framework
cannot generically load every aggregate (it is operation-specific), so this is a documented convention.

## Non-goals

No parallel validation framework; not every domain invariant becomes a derivation (domain logic still
protects invariants regardless of entry path, and the database still enforces concurrency/uniqueness);
no full paginated lookup grid runs at submit (an `Exists`, not a page load); a form never weakens an
operation rule; forms bound to one operation are never merged.
