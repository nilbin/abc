# 28 — Assignment & Grouping: domain by default, one closed scope set, plugins for the rest

Status: **settled by discussion** (supersedes the earlier "actor attributes" draft of this doc —
D-AA1…D-AA5 are resolved as: *don't*). This doc answers three questions that kept resurfacing:
why `all`/`own` are framework scope kinds but `where`/`shared` are not; where domain-specific
assignment (order participants, approval chains, crews) should live; and what happens if generic
groups à la Azure/Entra are ever wanted.

## The closed-set principle

The framework may only ship a scope kind whose **subject-side fact the framework itself owns**.
It owns exactly two facts about an actor: their **identity** and their **position in the tenant
tree**. That yields the complete, closed set:

| Scope | Subject-side fact | Owner |
| --- | --- | --- |
| `all` | none needed | — |
| `own` | actor id | framework |
| `subtree` / `inherited` | active node's path | framework |
| ~~`where(attr)`~~ | region/team/… | **domain data** → not a framework scope |
| ~~`shared`~~ | per-record grant | **domain data** → not a framework scope |

`own` passes the test with a nuance worth stating: the framework owns only the *toggle and the
enforcement symmetry* — what "own" means (which column holds the actor id) is still declared by
the domain, per view (`ScopedTo(context, permission, x => x.AssignedToActorId)`). And `all|own`
is precisely the one scope posture a tenant admin can meaningfully flip at runtime *without any
domain knowledge* — which is why the policy axis (docs/27 Axis 2) is worth having and why it
stops there. This set is **closed**: policies never grow new kinds.

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
// exactly as own-scoped operations re-check via CheckOwnership today.
```

What the framework contributes is what it already contributes for `own`: identity on the actor,
the declarative seam, and the discipline that read filters and write checks come from one
predicate so they cannot drift. (A generalized `CheckScope` twin of `CheckOwnership` is the one
small remaining affordance; the mechanism is otherwise complete.)

## If generic groups are ever wanted: the union seam and the ladder

Azure needs directory groups because it serves thousands of apps that cannot share a schema — a
lingua franca between IT and domain-blind software. Tam *is* the app; there is no domain-blind
layer. And the grouping needs it does have are already served three ways: the **tenant tree**
(hierarchy with clean container semantics: cascade, act-as, subtree/inherited), **roles**
(grouping capability), **policies** (grouping data reach). The only thing generic groups would
add is an indirection between accounts and their role/policy assignments.

The architectural insurance costs nothing because it already holds by construction: **actor
resolution is a union over membership sources, and `Actor` stays a flat grant set.** If groups
ever arrive they arrive as *one more source flattened into that union at resolution time* — never
as a new principal type threaded through enforcement, the manifest, or the analyzer.

The escalation ladder, each step gated on observed pain:

1. **Now — nothing.** Domain tables for domain groupings; tree/roles/policies for admin
   groupings. The docs/24 seat profile (tens of seats, not tens of thousands) produces no
   bulk-assignment pain.
2. **The same `{roles, policies}` bundle copy-pasted across many users** → add **membership
   profiles**: a named bundle applied at `users.define`/`users.invite`. Authoring sugar only; no
   member lists, no enforcement change.
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
stress test of the plugin architecture. The scenario, and the three genuine gaps it exposes
(config-driven gate targets, parking a blocked envelope across the rollback, sanctioned envelope
replay with dual-attribution audit), are drafted as tutorial Step 16
([20-tutorial.md](20-tutorial.md)).

## Decisions

- **D-AG1 — the closed scope set: `all`, `own`, `subtree`, `inherited`. Policies never grow new
  kinds.** A scope kind requires a framework-owned subject-side fact; there are no more of those.
- **D-AG2 — domain assignment lives in domain tables**, declared once per resource and enforced
  symmetrically (read filter + write re-check). No membership attributes, no framework share
  registry.
- **D-AG3 — groups, if ever, are an authoring-side indirection resolved into the existing actor
  union** — profiles first, flat groups on real cross-domain demand, nesting never in core.
- **D-AG4 — approval flows are never core.** They are a plugin's product, built on gates, parked
  envelopes and replay (tutorial Step 16 defines the required seams).
