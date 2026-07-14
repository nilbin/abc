# 27 — Authorization Model: Capability × Data Scope

## What this fixes

Today authorization is **one axis**: a role is a flat list of permission strings (`orders.read`,
`customers.create`), with a `:own` suffix bolted on for record ownership and `*` for "everything".
That conflates two questions a real access model must answer separately:

1. **Capability** — *what can you do, on which resource type?* ("full access to Orders, read-only on
   Customers, nothing on Billing")
2. **Data scope** — *which rows of that resource can you do it to?* ("all customers" vs "only my
   own" vs "only my region's subtree" vs "only records shared with me")

These are orthogonal. A dispatcher may **manage** Orders but only their **own**; a regional manager
may **view** Customers across a whole **subtree**. Collapsing them into one permission list can't
express that without a combinatorial explosion of strings, and it has no home for row rules beyond
`:own`. So Tam gets two independent grouping axes, layered.

## The layers

```
Tenant isolation   → which tenant's data exists for you at all      (global query filter, done)
Capability         → which actions on which resource types          (roles / permission sets)   AXIS 1
Data scope         → which ROWS of a resource each capability hits   (access policies)            AXIS 2
Field visibility   → which FIELDS of a row you may read/write        (capability field masks)     (opt-in)
```

A grant is only effective where **all** layers allow it: in-tenant AND capability-granted AND the
row in scope AND (if masked) the field visible.

## Axis 1 — Capability

### Resources declare actions; access levels bundle them

Each resource (Order, Customer, …) declares its **actions** — the atoms. CRUD-ish ones (`view`,
`create`, `edit`, `delete`) plus domain intents that aren't CRUD (`orders.complete`, `orders.assign`).
The permission strings we have today ARE these atoms; nothing is thrown away.

On top of the atoms sit **access levels** — ordered presets, so a role is authored ergonomically
instead of enumerating strings:

```
None  <  View  <  Edit  <  Manage
View   → view, list
Edit   → View + create, update
Manage → Edit + delete + the resource's admin/intent actions (complete, assign, configure fields…)
```

A **role** is then a map `resource → level` (with the escape hatch of naming individual actions when a
level is too coarse). "Full Orders, read Customers" is one role: `{ Order: Manage, Customer: View }`.
Levels expand to atoms at load time, so enforcement, the manifest, and the analyzer keep working on
the atom set exactly as today — levels are authoring sugar over a stable substrate.

### Field visibility (opt-in)

A resource may mark fields **sensitive** (e.g. `Customer.creditInfo`). A capability grant then carries
an optional field mask; a role can grant `Customer: View` while excluding sensitive fields. Read
masking drops the field from views/manifest; write masking rejects a `Change` to it. Off unless a
resource opts a field in — most resources never need it, and it's pure overhead until they do.

### Composition

Capability is the **union** across a membership's roles (more roles ⇒ more capability). No deny rules
(see Decisions) — deny is a footgun that turns "what can this user do" into a constraint-solve.

## Axis 2 — Data scope

A **scope** is a declarative row rule attached to a (resource, capability). Kinds, cheapest to richest:

- **all** — every row of the **active node** (node-local; today's default). "All" never means the
  whole tree — tree breadth is what `subtree`/`inherited` add explicitly.
- **own** — rows the actor owns; the resource declares what "own" means (today's `:own`, unchanged).
- **subtree** — rows owned at or **below** the active node in the hierarchy — the *downward* roll-up
  (a region manager over all its companies). Implemented as a semi-join against the (tiny) tenants
  table: `row.TenantId IN (SELECT Id FROM tenants WHERE Path LIKE active.Path || '%')` — rows carry
  only `TenantId`; **no per-row path denormalization** (see "Implementation notes" below).
- **inherited** — rows owned at or **above** the active node — the *upward* read of shared/reference
  data that is owned high and used by every leaf below it (a group-level price list or master catalog
  every company reads). Cheaper still: the active node's ancestor ids are enumerable from its own
  path, so this is a **bounded `row.TenantId IN (ancestor ids)` list** — no LIKE at all. Only ever
  exposes the active node's *own* ancestors' rows, never a sibling subtree, so isolation holds.
- **where(attribute)** — an attribute predicate the resource defines and the policy parameterizes
  (e.g. `region == actor.region`, `team ∈ actor.teams`). Declarative, translated into the query.
- **shared** — explicit per-record grants (a share table: this record shared with this account/team).
  Distinct from `inherited`: `inherited` is structural (ancestor-owned), `shared` is an ACL per row.

Scope is **per (resource, capability)**, so "view all Orders but manage only your own" is two grants
with different scopes on the same resource. Scopes **union** (broadest wins) when several grants cover
the same capability — `all ⊇ subtree ⊇ own`, and `shared`/`where` add rows.

### Grouping: access policies (the separate axis)

Scopes group into named **access policies**, assigned per membership independently of roles:

- `own-only` = every resource scoped to `own`.
- `regional` = Customers/Orders scoped to `subtree` of the member's tenant.
- `full` = everything `all`.

So the two groupings are: **roles** (capability) and **access policies** (data scope). A membership
picks from each menu — the same role reused with different policies across memberships.

### Enforcement

- **Reads**: the view's declarative scope (generalizing today's `ScopedTo`) compiles the resolved
  scope into the query — `own` → owner predicate, `subtree` → path prefix, `where` → attribute
  predicate, `shared` → join the share table. One line per view, same as today.
- **Writes**: the operation re-checks the scope on the target row authoritatively (generalizing
  today's `CheckOwnership`) — a stale/forged id can't escape scope.
- Both ride the existing pipeline; the row rules live in one resolver, not per operation.

## The hierarchy write model (settled): grants fan out, writes fan in

This is the model chosen after a design review (prior-art + formal + implementation + UX). It is the
answer to "how do capability and create-target behave across the tenant tree."

**Reads and grants fan *out* over the tree; every write fans *in* to one node.** Formally: a read or
update targets an *existing* row, so its owning tenant is read off the row — always total and
unambiguous. A create targets a row that does not exist yet, so its tenant is a free variable that
must be *bound*; data-scope (a predicate over existing rows) cannot bind it. Therefore:

- **Create-target invariant.** Every create fans in to exactly **one explicit target node**, and the
  row is stamped with it; the target is **never inferred from a rolled-up read view**. Eligibility is a
  **capability** question, not a data-scope one: data-scope is a predicate over *existing* rows and is
  vacuous at create time, so the eligible-target set is `{ t : create ∈ Cap_eff(t) }` — the nodes where
  the actor's (cascaded) capability grants create. The target is an explicit input that **defaults to
  the active node**:
  - Single-node actor (create capability only at the active node): the target *is* the active node,
    no prompt — a leaf worker never sees a chooser.
  - Subtree actor (an admin holding a cascading `X.create` at/above the active node): the create form
    surfaces a **target-node field** — a lookup over the eligible targets within the active subtree —
    so they create an order **in a sub-company without switching active company** (the Azure "pick the
    resource group" / GitHub "pick the owner" pattern). The server validates the chosen target against
    the capability cascade and rejects a forged/out-of-scope target.

  **The target is the operation's execution tenant, not just a column value.** An operation produces
  more than the row: audit entries, outbox events/effect broadcasts, idempotency records, and in-form
  lookups (picking the sub-company's customer) all carry a tenant. If only the row were stamped with
  target *C* while the request ran ambient at parent *P*, the audit would land in *P*'s trail, the SSE
  effect would fire on *P*'s channel (so *C*'s grids never refresh), the idempotency key would scope to
  *P*, and lookups would show *P*'s data. So: after validating the target, the server **rebinds the
  ambient tenant scope to the target for the duration of the request** (submit AND the form's
  resolve/lookup calls carry it). Then the stamp interceptor, audit, outbox, effects, idempotency and
  lookups all land coherently in *C* — and no handler hand-writes `TenantId`, preserving the DRY
  guarantee the interceptor exists for.

  Capability stays **flat**: `X.create` is one bit in the manifest ("can create X"), gated once;
  *which* nodes are eligible comes from a lookup over the capability cascade — not per-node capability
  in the manifest. So this does NOT touch the `actorPermissions` → typed-client → UI-gating chain.
  What is rejected is per-node create *buttons* (one gated control per node), which would pluralize
  that flat set for no gain; the target-as-field keeps it singular. Switching the active node remains
  available but is never required to create in a node within your cascaded reach.

  Two deliberate asymmetries, stated so they aren't filed as bugs:
  - **Admin-down ≠ consultant-up.** The no-switch target field appears only when the cascading grant
    is held **at or above** the active node (capability flows down). The mirror persona — read-only at
    the parent, full access granted **at a child** — must still switch context to create there,
    because grants never flow up: standing at the parent, their flat set has no `create` and no button
    renders. Consistent, intentional, and the tenant picker offers both contexts at login.
  - **The target lookup is active-subtree only.** Holding create via an unrelated membership in a
    sibling tree does not surface that node in the form's picker — creating there is a context switch.
    A deliberate simplification: one subtree per standpoint.

- **Capability across the tree** is the union up the active node's **cascading** ancestor memberships,
  collapsed to **one flat set** at the active node — so `Actor.Can`, the manifest, and the UI gating
  are unchanged (a bigger flat set, nothing more). Grants never flow up; downward flow is opt-in via a
  membership's cascade flag (the write-side mirror of `subtree`).

  **Role names resolve in their own tenant.** A membership's role names bind to the role definitions
  of **that membership's node**, not the active node's. A region membership naming `dispatcher` means
  the region's `dispatcher`; resolving it through the active node's role table (which the global
  filter would do naively) silently finds nothing and the cascaded grants evaporate. The resolver
  therefore loads each ancestor membership's roles **tenant-qualified** (bypassing the ambient filter),
  and role names are **never merged across levels** — a leaf defining its own `dispatcher` is a
  different role from the region's; each membership's names bind only to its own node's definitions.

- **Ownership is at any level, domain-decided — there is no framework "leaf".** Transactional rows
  (orders) get `create` capability only at operating nodes; shared reference rows (catalogs) get it
  only at the group node. A row lives wherever it was created; descendants read shared rows via the
  `inherited` scope. "Leaf" (if a domain wants the concept) is *a node with no children* — computed,
  never a level number, and it changes as the tree grows.

- **Editing a shared (ancestor-owned) row** happens at its **owner node** — you switch up to manage
  the group catalog (same fan-in invariant, upward). A manager editing a rolled-up *descendant* row is
  the separate opt-in `subtree`-write case (the write-side scope re-check, net-new alongside `own`).

- **Dynamic, per-tenant depth** is native to the materialized `Path` (arbitrary N levels). "Leaf" is
  never a level number — it is *a node with no children*, computed, and it changes as the tree grows.
  Re-parenting a subtree rewrites `Path` values **in the tenants table only** (see below) — rows are
  untouched, so a re-org is a small transaction, not a data sweep.

### Implementation notes (settled at review)

- **No per-row path denormalization.** Rows carry only `TenantId`; tree filters go through the
  (tiny, effectively cached) tenants table. `subtree` = semi-join on `Path LIKE active.Path || '%'`;
  `inherited` = a bounded `TenantId IN (ancestor ids)` list computed from the active node's own path.
  This keeps every row single-tenant-keyed on existing indexes and makes **re-parenting nearly free**:
  moving a subtree rewrites the moved nodes' `Path` values in `tenants` and nothing else.
- **Cross-node create = ambient rebind.** The validated target node becomes the request's execution
  tenant (see the create-target invariant above): one mechanism, and audit/outbox/effects/idempotency/
  lookups stay coherent with the row. No per-handler `TenantId` assignments.
- **Cross-level role resolution is tenant-qualified** (see "Capability across the tree"): each
  membership's role names load from that membership's node, bypassing the ambient filter; no
  cross-level name merging.
- **Seats vs cascade** (docs/24): a cascading membership consumes **one seat at its attachment node**
  while reaching its whole subtree — one region membership over 50 companies is one seat. Acknowledged
  and accepted for now; if pricing should track reach rather than attachment, that is a docs/24 change
  (e.g. counting cascaded reach), not an authorization change.

## How it binds to identity (docs/26)

```
Account ──< TenantMembership(account, tenant) ──┬── roles          (capability)
                                                └── access policies (data scope)
```

A membership is the join of an account to a tenant, carrying **both** axes. Different tenants → different
roles and scopes for the same person. The hierarchy (docs/26): a membership attached at a parent node
plus `subtree` scope is exactly the roll-up a regional manager needs; a membership at a leaf with `own`
scope is a line worker. Seats (docs/24) count memberships per tenant.

## Backward compatibility

- Today's permission strings remain the atoms; existing roles (flat permission lists) are valid as
  "explicit action" grants with implicit `all` scope.
- `:own` becomes the `own` scope kind; `*` becomes `Manage` on every resource with `all` scope.
- The manifest keeps shipping the actor's effective capability (and now scope) so the UI and typed
  client keep hiding/showing on it. TAM004-style analyzer rules can later assert every resource
  declares its actions/levels.

## Decisions (settled)

- **D-A1 — access levels: YES.** `None/View/Edit/Manage` presets are the authoring model over the
  existing permission atoms (sugar; nothing downstream changes).
- **D-A2 — row-scope kinds: `all`, `own`, `subtree`, `inherited`, `where` now; DEFER `shared`
  (record ACLs).** `inherited` (upward, ancestor-owned shared data) was added at the hierarchy review.
  Per-record sharing is a per-domain design later, not a framework primitive yet.
- **D-A3 — field-level: FULL now (read + write masking).** Resources opt a field in as sensitive; a
  grant can hide it from views/manifest (read) AND reject a `Change` to it (write).
- **D-A4 — deny rules: NO.** Union-grant only.
- **D-A5 — scope combination: UNION** (broadest wins) for the same capability.

## Sequencing note

This rides the identity work (D-H2 = platform-global accounts + memberships), since memberships carry
both axes. So the build order is: Account + TenantMembership + hierarchy path → this capability/scope
model on the membership → PKCE issuing account-subject tokens that name the active tenant + resolved
grants.
