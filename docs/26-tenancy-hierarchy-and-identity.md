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

### Two scoping modes, declared per view/operation

- **Strict** (default, today's behaviour): a request scoped to tenant *T* sees only *T*'s own rows.
- **Inherited**: a request scoped to *T* sees rows of *T* and every descendant — "roll-up" reads
  (a region manager's dashboard over all its companies).

The global query filter generalizes cleanly. Instead of `row.TenantId == current`, the ambient
context carries the **current tenant's path**, and the filter is:

- strict: `row.TenantId == current.Id`
- inherited: `row.Path (of row's tenant) starts-with current.Path` — i.e. `row.TenantPath LIKE
  current.Path || '%'`, backed by an index on the denormalized `TenantPath` we copy onto each row
  (or a join to the tenant table; denormalizing the path onto the row keeps reads single-table).

Writes still stamp the **leaf** tenant (the `TenantStampInterceptor` is unchanged — a row is
created *in* one tenant). Inheritance is a read-time widening, never a write-time ambiguity.

**Decision D-H1 — default scope.** Strict by default, `inherited` opt-in per view, because silent
roll-up is a data-exposure footgun; a view asks for it explicitly. (Recommended.)

### Permissions across the hierarchy

A grant made at *T* can be declared to **cascade** to descendants or not — the same strict/inherited
axis, applied to authorization. A "region admin" role granted at the region cascades; a
"company-local clerk" does not. This rides the existing `Actor` — the actor resolved for a request
already carries its permission set; hierarchy only changes *where the grant was attached* and
whether it flows down.

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

## Open decisions

- **D-H1** default hierarchy scope: strict vs inherited — *recommend strict, inherited opt-in.*
- **D-H2** account ownership: Option 1 / 2 / 3 — *recommend Option 1 (platform-global + memberships).*
- **D-H3** active-tenant selection mechanism (subdomain / path / in-app switcher) — deferred until
  D-H2 lands; the PKCE flow will surface it (a login can land on a tenant picker when the account has
  several memberships).
