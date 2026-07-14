# 26 — Hierarchical Tenants, Cross-Tenant Users, and Who Owns the Account

## What this reopens

docs/01 and the tenant work in docs (the global query filter, the ambient `TenantScope`, the
`tam:tenant` token claim) all assume **one flat tenant per row and one tenant per user**. Two new
requirements break that assumption, and they break it in the foundation, so they need a decision
before more is built on the current shape:

1. **Hierarchical tenants** — a tenant can nest N levels (group → region → company → department).
2. **Cross-hierarchy user access** — one human can act in several tenants that are *not* in the same
   subtree (a consultant serving ten unrelated customers; an auditor across regions).

Requirement 2 forces the question the current model never had to answer: **who owns the user
account?** Today `TamUserEntity` is tenant-scoped — a user *is* a row inside a tenant. A user who
belongs to many unrelated tenants cannot be a row inside one of them.

## Part A — Hierarchical tenants

### Model

A tenant gains a parent and a **materialized path** — the ordered chain of ancestor ids, stored on
the row:

```
Tenant { Id, ParentId?, Path }        // Path = "acme" | "acme.eu" | "acme.eu.sales"
```

Every tenant-scoped row keeps its single owning `TenantId` (unchanged — no row moves). Membership
"in a subtree" is a **prefix test on the path**, which is one indexed range scan, not a recursive
CTE per query.

### Three scoping modes, declared per view/operation

- **Strict** (default): a request at node *T* sees only *T*'s own rows.
- **subtree** (downward roll-up): *T* sees rows of *T* and every **descendant** — a region manager's
  dashboard over all its companies.
- **inherited** (upward shared): *T* sees rows of *T* and every **ancestor** — shared/reference data
  owned high and used by every leaf below (a group-level price list every company reads).

The global query filter generalizes cleanly. The ambient context carries the **current node's path**,
and the widened filters resolve tenant sets through the **tenants table** — rows keep carrying only
`TenantId`, nothing is denormalized onto them:

- strict: `row.TenantId == current.Id`
- subtree (down): `row.TenantId IN (SELECT Id FROM tenants WHERE Path LIKE current.Path || '%')` —
  a semi-join against the tiny, effectively-cached tenants table (indexed on `Path`), then the row's
  existing `TenantId` index does the work.
- inherited (up): `row.TenantId IN (ancestor ids)` — the ancestor set is enumerable from the current
  node's own path, so it's a bounded IN-list; no LIKE at all.

This is deliberately **not** the denormalize-`TenantPath`-onto-every-row design: keeping rows
single-tenant-keyed avoids a new column + index on every scoped table and makes **re-parenting nearly
free** — moving a subtree rewrites `Path` values in the tenants table only, never a row sweep.
`inherited` only ever exposes the active node's *own* ancestors' rows — never a sibling subtree — so
isolation holds.

Writes always stamp the **active node** (`TenantStampInterceptor` unchanged — a row is created *in*
the node the actor stands in). Any inheritance is a read-time widening, never a write-time ambiguity;
this is the D-H4 create-target invariant (grants fan out, writes fan in).

**Decision D-H1 — default scope.** Strict by default; `subtree`/`inherited` are opt-in per view,
because silent roll-up is a data-exposure footgun and a view should ask for breadth explicitly.

### Permissions across the hierarchy

A grant made at *T* can be declared to **cascade** to descendants or not — the same strict/inherited
axis, applied to authorization, decided **per role assignment** (D-H5). A "region admin" role granted
at the region cascades; a "company-local clerk" on the same membership does not. This rides the existing `Actor` — the actor resolved for a request
already carries its permission set; hierarchy only changes *where the grant was attached* and
whether it flows down.

**Grants flow down, never up (by construction).** Access to a tenant exists only where a membership
row exists, so a membership attached at a *child* node grants that child and nothing above it: **full
custom access to a sub-level with no access to the parent is the default**, not a special case (a
consultant on `acme.eu.sales` sees exactly that subtree and never `acme.eu` or `acme`). Cascade is the
opt-in that widens a *parent* membership **downward** into descendants (the `subtree` read scope,
[docs/27](27-authorization-model.md)); there is no mechanism that widens a child membership upward.
This asymmetry — downward roll-up is opt-in, upward is impossible — is exactly what makes leaf-scoped
and roll-up roles both expressible on one hierarchy.

## Part B — Who owns the account (the fork)

Three shapes. This is the decision that changes the most code, so it's stated plainly.

### Option 1 — Platform-global identity + memberships (recommended)

An **Account** is owned by the platform, not any tenant. Credentials/email are global and unique
platform-wide. Access is a join:

```
Account       { Id, Email, Credentials, DisplayName }         // global, no TenantId
TenantMembership { AccountId, TenantId, RolesJson }           // one row per (user, tenant) grant
```

- A user is **one** account with N memberships across **any** tenants, related or not — exactly
  requirement 2.
- Login authenticates the **account**; the request then selects an **active tenant** from the
  account's memberships (subdomain, path, or an in-app switcher). The `Actor` is built from *that
  membership's* roles.
- `Account` is **not** `ITenantScoped` (it's global); `TenantMembership` is the tenant link and is
  scoped. The global filter is unaffected for domain data — only the identity tables change.
- This is the GitHub / Google-Workspace / Slack-Enterprise-Grid model. It's the only one of the
  three that natively satisfies "access to multiple tenants not in the same hierarchy."

**Cost:** `TamUserEntity` (tenant-scoped) splits into a global `Account` + scoped `TenantMembership`;
auth resolves the account then the active-tenant membership; the `tam:tenant` token claim becomes
"account identity + the set/selection of accessible tenants." Seat counting moves to per-tenant
membership counts. Invites replace in-tenant user creation (you invite an email; it links or creates
the global account).

### Option 2 — Home-tenant + guest access

A user is owned by a **home tenant** and can be granted **guest** memberships elsewhere (Azure AD
B2B). Identity is still rooted in a tenant; cross-tenant access is an explicit guest link.

**Cost:** similar membership join, but identity stays tenant-rooted — simpler migration from today,
but "who owns the consultant with no natural home" is awkward, and cross-org identity collisions
(same email, two homes) are a real edge.

### Option 3 — Tenant-owned users (today)

Each tenant owns its users; the same human in two tenants is two unrelated accounts. **Does not
satisfy requirement 2** — listed only for completeness. Keep it only if cross-hierarchy access is
dropped.

### How the token changes (gates PKCE)

The PKCE switch (docs: auth) must encode identity per the choice above, so it waits on this:

- Option 1: the token subject is the **account**; it carries the accessible-tenant set (or the
  selected active tenant per session), and the request's active tenant must be one the account is a
  member of — the tenant-binding check generalizes from "== token tenant" to "∈ token's memberships".
- Option 2/3: the token stays tenant-rooted (closer to today's `tam:tenant`), with guest tenants
  listed for Option 2.

**Decision D-H2 — account ownership.** Pending (this doc's open question). Recommendation: **Option 1**,
because requirement 2 (cross-hierarchy access) is a hard requirement and only Option 1 meets it
without contortion, and it composes cleanly with the hierarchy in Part A (memberships attach at any
node; roll-up is the Part-A inherited scope).

## Migration & blast radius (Option 1)

- **Additive first:** introduce `Account` + `TenantMembership` alongside `TamUserEntity`; a shim
  resolves the current single-tenant user as an account with one membership, so nothing breaks while
  the surfaces move.
- **Auth:** `ClaimsActorProvider` resolves account → active-tenant membership → roles. The token
  server (post-PKCE) issues account-subject tokens.
- **Global filter:** unchanged for domain tables. Identity tables (`Account`) are global; membership
  is scoped.
- **Seats/entitlements (docs/24):** counted per tenant over memberships, not over a tenant-local
  users table.
- **Analyzer:** `Account` is deliberately *not* `ITenantScoped`, so TAM004 won't (and shouldn't)
  flag identity lookups; membership lookups stay scoped.

## Decisions (settled)

- **D-H1 — default read scope: STRICT, roll-up opt-in per view (revised).** A view sees only the
  active node's own rows unless it explicitly opts into `subtree` (downward roll-up) or `inherited`
  (upward shared-data read). *This reverses the earlier "inherited by default"* — the design review
  found inherited-default couples read-breadth to data exposure (a parent-node membership silently
  rolls up unless every sensitive resource remembers to narrow itself), and roll-up is net-new work
  regardless, so making breadth an explicit per-view choice is safer and no more code. A purpose-built
  "region dashboard" view opts into `subtree`; a shared-catalog view opts into `inherited`; everything
  else stays strict.
- **D-H4 — hierarchy write model: grants fan out, writes fan in.** A created row is stamped with **one
  explicit target node**, never inferred from a rolled-up view. Eligibility is a **capability**
  question (the nodes where the actor's cascaded capability grants `create` — data-scope is a predicate
  over existing rows and cannot bind a create target). The target defaults to the active node; an admin
  holding a cascading `X.create` picks the sub-company in a **target-node form field**
  (server-validated) — creating in a sub-company **without switching active company** (the Azure
  resource-group / GitHub owner pattern). **The validated target becomes the request's execution
  tenant** (ambient rebind): audit, outbox/effects, idempotency and in-form lookups all land in the
  target with the row — stamping only the row would strand those side-artifacts in the parent (audit in
  the wrong trail, SSE on the wrong channel, lookups showing the wrong node's data). Capability stays
  **flat** (`X.create` is one gated bit; eligible nodes come from a lookup over the cascade), so this
  does not touch the manifest→TS→UI-gating chain; per-node create *buttons* remain rejected. Capability
  at the active node is the union up its cascading ancestor memberships, collapsed to one flat set,
  with each membership's role names resolved **in its own tenant** (docs/27). Rows may be owned at any
  level (no framework "leaf"); shared reference data owned high is read by leaves via `inherited` scope
  and edited at its owner node. Two stated asymmetries: a grant held **at a child** never surfaces a
  no-switch create at the parent (grants don't flow up — switch context instead), and the target lookup
  covers the **active subtree only** (an unrelated sibling-tree membership means switching). Full
  rationale + scope kinds: [docs/27 — "The hierarchy write model"](27-authorization-model.md).
- **D-H2 — account ownership: Option 1 (platform-global `Account` + `TenantMembership`).** Global
  identity, access via memberships; the only model that supports a user across unrelated tenants.
- **D-H3** active-tenant selection — **implemented via the PKCE flow.** The framework-rendered
  `/connect/authorize` login lands on a tenant picker when the account has more than one membership
  (auto-selects the sole one otherwise); the chosen tenant is carried on the token's `tam:tenant`
  claim and turned into the request's scope by `ClaimTenantProvider`. Switching tenants = re-running
  the flow and picking another. (Subdomain/path selection remain open alternatives for later.)
  **Hierarchy note (Stage 3/4):** with cascade, the set of standable nodes is *memberships plus every
  descendant of a cascading membership* — the picker must therefore offer drill-down/search into
  cascaded descendants, not just list membership rows, or admins can't stand where they're allowed to
  act. Nodes are labeled by path (e.g. "Acme ▸ EU ▸ Sales") and each distinct effective-grant context
  is offered — never collapsed to "the highest".
- **D-H5 — cascade granularity: PER-ROLE (settled).** Cascade is carried **per role assignment** on
  the membership — `roles: [{name, cascade}]` — not one membership-wide boolean. A region admin's
  `orders-manager` role cascades to descendants while their `users-admin` role stays node-local, on
  the same membership. The resolver's ancestor walk unions only the *cascading* role assignments of
  ancestor memberships (each resolved in its own tenant, per docs/27); non-cascading assignments
  contribute at their attachment node only. `TenantMembershipEntity.RolesJson` is born in this shape
  in Stage 3 (current flat `["name"]` seeds read as `cascade: false`).

The membership row carries **both** authorization axes — capability (roles) and data scope (access
policies) — designed in [docs/27](27-authorization-model.md).
