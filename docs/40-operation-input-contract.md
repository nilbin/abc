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
  the view's Query + Filterable fields and fail closed on a typo — an unknown key cannot silently
  widen the universe.
- `DerivationResult.Lookup(...)` is recorded as a binding; submit runs `ContainsAsync` for each
  non-null lookup-bound field (the value read from the deserialized input, so a `Change<T>` edit field
  unwraps correctly) and blocks with the `invalid` finding when the value is outside the universe. The
  same `RunDerivationsAsync` — ALL derivations, every call — feeds resolve and submit, so they cannot
  drift. Resolve surfaces the binding as a `ResolvedLookup` (view + base filters) the picker scopes
  itself by, so the client browses exactly the candidate universe rather than materializing it.
- The small-set twin is `RequireOneOf(field, options, invalid)` — authoritative closed inline options,
  enforced at submit the same way. `AddOptions(...)` (no finding) stays advisory: offered at resolve,
  never enforced.

## The authority boundary: pipeline-rejects, not commit-stable

Authoritative derivations and lookup membership run **before the transaction opens**. "Authoritative"
here means *the operation pipeline rejects this request when it is evaluated* — NOT *the underlying
database fact cannot change between validation and commit*. A project could be closed, or a candidate
row deleted, in the window between the membership `Exists` and the handler's commit. That is by design
and must stay explicit: race-sensitive invariants still need domain/handler revalidation, concurrency
tokens (`IVersioned`), locks/leases, or database constraints — the derivation is a fast, honest
front-door check, not the last line of defence. The create-order sample keeps its handler-side
project check for exactly this reason; the derivation does not replace it.

## Non-goals

No parallel validation framework; not every domain invariant becomes a derivation (domain logic still
protects invariants regardless of entry path, and the database still enforces concurrency/uniqueness);
no full paginated lookup grid runs at submit (an `Exists`, not a page load); a form never weakens an
operation rule; forms bound to one operation are never merged.
