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

- **all** — every row in the tenant. (Today's default.)
- **own** — rows the actor owns; the resource declares what "own" means (today's `:own`, unchanged).
- **subtree** — rows whose tenant is at or below the actor's tenant in the hierarchy (docs/26 —
  the path-prefix test). This is what makes "inherited" reads a scope, not a special case.
- **where(attribute)** — an attribute predicate the resource defines and the policy parameterizes
  (e.g. `region == actor.region`, `team ∈ actor.teams`). Declarative, translated into the query.
- **shared** — explicit per-record grants (a share table: this record shared with this account/team).

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

## Open decisions (recommendations)

- **D-A1 — access levels vs raw permissions.** Introduce `None/View/Edit/Manage` presets as the
  authoring model over the existing atoms. *Recommend yes* — ergonomic, and it's sugar, so nothing
  downstream changes.
- **D-A2 — row-scope kinds for v1.** `all`, `own`, `subtree`, `where`. *Recommend yes*; **defer
  `shared` (record ACLs)** — it needs a share table and UI and is a distinct feature.
- **D-A3 — field-level.** *Recommend: build the seam and read-masking now* (resources opt fields in),
  defer write-masking. Or defer field-level entirely if no near-term need — this is the one that most
  changes scope, so it's a real fork.
- **D-A4 — deny rules.** *Recommend no* — union-grant only; revisit if a concrete need appears.
- **D-A5 — scope combination.** Union (broadest wins) for the same capability. *Recommend yes.*

## Sequencing note

This rides the identity work (D-H2 = platform-global accounts + memberships), since memberships carry
both axes. So the build order is: Account + TenantMembership + hierarchy path → this capability/scope
model on the membership → PKCE issuing account-subject tokens that name the active tenant + resolved
grants.
