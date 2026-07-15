# 28 — Assignment & Grouping: domain by default, one closed scope set, plugins for the rest

Status: **settled and BUILT** (supersedes the earlier "actor attributes" draft — D-AA1…D-AA5
resolved as: *don't*). This doc answers the questions that kept resurfacing: which row scopes are
the framework's at all; where domain-specific assignment (order participants, approval chains,
crews) lives; and what happens if generic groups à la Azure/Entra are ever wanted. The final push
went further than the draft: even `own` left the policy vocabulary — the access-policy registry is
RETIRED and ownership is the compile-enforced **paired-atom pattern** below.

## The closed-set principle (both ends)

The framework may only ship a row scope over facts it owns on **both ends** — the subject side
(what the actor is) and the object side (what the row is). It owns exactly one such dimension:
**tenancy** — the actor's tree position and the framework-stamped `TenantId` on every row.

| Scope | Subject fact | Object fact | Verdict |
| --- | --- | --- | --- |
| `subtree` / `inherited` | tree position (fw) | `TenantId` (fw-stamped) | **framework scope** |
| `own` | actor id (fw) | owner column (**domain**) | **paired-atom pattern** (below) |
| `where(attr)` | region/team (**domain**) | tagged column (**domain**) | domain pattern |
| `shared` | — | share edge (**domain**) | domain pattern |

`own` fails the object-side half — which column means "mine" is domain knowledge — and that is
exactly why policy-authored `own` could not be made fail-closed: whether a grant carried the scope
was runtime data no analyzer could tie to per-view discipline. So ownership moved to the
**capability axis as the paired-atom pattern**: the base atom (`orders.read`) is own-scoped by
default; a declared widening atom (`[Widens("orders.read-all")]`) lifts it. The domain declares
the predicate once — `ScopedUnless(context, "orders.read-all", x => x.AssignedToActorId)` on reads,
`CheckOwnershipUnless` on writes — and **TAM006** verifies compilation-wide that the widening atom
is declared (so roles can grant it; levels expand `X-all` on X's tier) and that every view on the
base atom applies the scope. The tenant admin keeps the runtime toggle — grant a role with or
without the widening atom — with none of the silent-fail-open risk. The access-policy registry
(built for `all`|`own` in v1) is retired; the tree scopes stay per-view declarations.

## Domain assignment is domain state

Order participants, time-approval groups, service crews: these are join tables with their own
semantics (a participant has a role *on that order*; a chain has sequence and quorum). A
framework-level tag can't carry those semantics, so the domain builds the real table anyway — and
any framework copy (membership attributes, share registries) becomes a denormalization with a
sync problem. Every such relationship is already resolvable through domain tables keyed by
`actor.Id`, the one subject-side fact the framework provides.

**The pattern** (all seams exist): declare the predicate once and use it on both sides —

```csharp
// Read side: the view declares the assignment predicate — a join, not a tag.
db.Orders.Where(o => db.OrderParticipants
    .Any(p => p.OrderId == o.Id && p.ActorId == actorId))

// Write side: the operation re-checks the SAME predicate authoritatively before mutating,
// exactly as own-scoped operations re-check via CheckOwnershipUnless.
```

What the framework contributes is what it contributes for `own`: identity on the actor, the
declarative seam (`ScopedUnless`/`CheckOwnershipUnless` accept any predicate — a participant
subquery as readily as an owner column), and the discipline that read filters and write checks
come from one predicate so they cannot drift.

## If generic groups are ever wanted: the union seam and the ladder

Azure needs directory groups because it serves thousands of apps that cannot share a schema — a
lingua franca between IT and domain-blind software. Tam *is* the app; there is no domain-blind
layer. And the grouping needs it does have are already served two ways: the **tenant tree**
(hierarchy with clean container semantics: cascade, act-as, subtree/inherited) and **roles**
(grouping capability — row reach included, via the widening atoms). The only thing generic groups
would add is an indirection between accounts and their role assignments.

The architectural insurance costs nothing because it already holds by construction: **actor
resolution is a union over membership sources, and `Actor` stays a flat grant set.** If groups
ever arrive they arrive as *one more source flattened into that union at resolution time* — never
as a new principal type threaded through enforcement, the manifest, or the analyzer.

The escalation ladder, each step gated on observed pain:

1. **Now — nothing.** Domain tables for domain groupings; the tree and roles for admin
   groupings. The docs/24 seat profile (tens of seats, not tens of thousands) produces no
   bulk-assignment pain.
2. **The same role bundle copy-pasted across many users** → add **membership profiles**: a named
   bundle applied at `users.define`/`users.invite`. Authoring sugar only; no member lists, no
   enforcement change.
3. **Two or more unrelated domains referencing the same people-set** (a "crew" used by orders AND
   time AND inventory) → promote that set to a platform group: flat member list, **never
   nested**, resolved as one more source in the actor union.
4. **Never in core**: nested people-groups (hierarchy with murky semantics — the tree is the
   framework's hierarchy, or nobody's) and generic approval engines (a workflow product; see
   below).

This generalizes D1's own revisit clause: *arrive as a new membership source, not a directory.*

## Approval flows and rich group systems: plugin territory

An approval flow is a state machine (pending state, delegation, escalation, timeouts). Generic
workflow in core collides with the "not a low-code platform" non-goal — but it is exactly
**plugin-shaped**: plugins already ship their own entities, operations, views, gates, effect
subscribers, locales and per-tenant activation, and the email seam gives them notifications. An
opinionated nested-group + approvals system can therefore exist as something a tenant *installs*,
gating existing domain operations **without the domains knowing** — which doubles as the sharpest
stress test of the plugin architecture. The three genuine gaps that scenario exposed are now
framework seams, each proven through the real pipeline in the test suite: **config-driven gate
targets** (`GateDefinition.Wildcard` — the gate runs on every operation, receives
`gate.OperationId`, and decides from its own rules), **parking a blocked envelope across the
rollback** (`gate.Park(work)` — the domain transaction rolls back first, then the parked work
commits in a fresh scope pinned to the same tenant; discarded if the gate allows), and
**sanctioned envelope replay** (`EnvelopeReplay` — full-pipeline re-execution as the original
initiator with grants re-resolved as of now, `InvocationSource.Workflow` as the sanction, the
envelope id as both audit correlation and initiator-scoped idempotency key — dual attribution
without a second actor field). The walkthrough is tutorial Step 16
([20-tutorial.md](20-tutorial.md)).

## Decisions

- **D-AG1 — framework row scopes are the tenancy dimension only (`subtree`/`inherited` as
  per-view declarations); the access-policy registry is RETIRED.** A framework scope kind
  requires framework-owned facts on BOTH ends; tenancy is the only such dimension.
- **D-AG2 — ownership is the paired-atom capability pattern** — base atom own-scoped, `[Widens]`
  atom lifts, `ScopedUnless`/`CheckOwnershipUnless` at the seams, TAM006 enforcing both
  directions — and richer domain assignment lives in domain tables, declared once and enforced
  symmetrically. No scope suffixes, no membership attributes, no framework share registry.
- **D-AG3 — groups, if ever, are an authoring-side indirection resolved into the existing actor
  union** — profiles first, flat groups on real cross-domain demand, nesting never in core.
- **D-AG4 — approval flows are never core.** They are a plugin's product, built on gates, parked
  envelopes and replay. The seams themselves ARE core and are built: `GateDefinition.Wildcard`,
  `GateContext.Park`, `EnvelopeReplay` (tutorial Step 16 walks the scenario).
