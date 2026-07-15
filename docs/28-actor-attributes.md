# 28 — Actor Attributes: the design `where` scopes wait on

Status: **DRAFT — decision-ready.** docs/27 deferred the `where(attribute)` scope kind ("region ==
actor.region", "team ∈ actor.teams") until actor attributes had a design, and `shared` (per-record
ACLs) behind it. This doc proposes the smallest model that makes `where` real without inventing a
second authorization system. Decisions D-AA1…D-AA5 at the end are the open choices.

## The problem

The `own` scope works because every actor carries exactly one attribute for free: their identity
(`AssignedToActorId == actor.Id`). A `where` scope needs MORE facts about the actor — their region,
their team(s), their branch — and today the actor has nowhere to carry them: `Actor` is
`(Id, DisplayName, Grants)`. Three questions fall out:

1. **Where do attribute VALUES live?** (on the membership, like roles/policies?)
2. **Where do attribute NAMES come from?** (free-form strings, or a registry like the permission
   catalogue?)
3. **How does a predicate bind an attribute to a resource FIELD?** (who declares that Orders have a
   `region`, and that it is comparable to `actor.region`?)

## Proposal

### 1. Values: a JSON map on the membership (per-tenant, like everything else about access)

```
TenantMembershipEntity + AttributesJson   // {"region":"north","teams":["service","installs"]}
```

The same person can be `region: north` in one tenant and `region: all-se` in another — attributes
are facts about the MEMBERSHIP, not the account, exactly like roles and policies (docs/26: the
account is global, access is per-tenant). Values are strings or string arrays; nothing else.
`users.define`/`users.invite` gain an optional `Attributes` input; the admin UI reuses the
key-value editor. Cascade: a cascading membership carries its attributes into descendants
unchanged (the membership is one grant surface; its facts travel with it).

### 2. Names: declared by the RESOURCE, not free-form (registry-as-compiler again)

A `where` predicate is only meaningful if the resource has the matching field. So the resource
declares its scopeable attributes exactly the way views declare scopes today — in code, one line:

```csharp
[View("orders.list")] …
    db.Orders.InNode(context.TenantId)
       .ScopedTo(context, "orders.read", x => x.AssignedToActorId)      // own (today)
       .AttributeScope(context, "orders.read", "region", x => x.Region) // where (new)
```

`AttributeScope(attribute, selector)` registers `(resource: orders, attribute: region, field)` into
the compiled catalogue. `policies.define` then validates a `where` scope the same way it validates
resources and scope kinds today: `{"orders": "where:region"}` is legal only if orders DECLARED
`region`. Typos die at define time with a localized finding — the same fail-closed
registry-as-compiler pattern as levels, atoms and extension fields. No new DSL: the predicate is
always `field == attr` (string) or `field ∈ attr` (array attribute), decided by the attribute's
declared shape.

### 3. Resolution: the same narrowing seam Axis 2 built

`ClaimsActorProvider` already narrows each membership's grants by its policies before the union.
A `where` scope rides the same seam: the grant is suffixed `orders.read:where:region=north` — the
VALUE is baked in at actor-resolution time from that membership's `AttributesJson` (re-read per
request, so an attribute edit takes effect immediately, like a role edit). Enforcement is the
existing suffix machinery generalized: `Actor.Scope("orders.read")` returns the parsed scope, and
`AttributeScope` compiles `x => x.Region == "north"` (or `Contains`) into the query — translated
SQL, no client-side filtering, same as `own` today.

- **Union stays broadest-wins** (D-A5): `all` beats `where`, `where` beats `own`? NO — `where` and
  `own` are not ordered; they UNION as row-sets: `(x.AssignedToActorId == me) OR (x.Region ==
  "north")`. Only `all` short-circuits.
- **Missing attribute fails CLOSED**: a membership whose policy says `where:region` but whose
  `AttributesJson` has no `region` contributes ZERO rows for that grant (not all rows) — the
  canonicalization lesson from the casing bug, applied in advance.
- **Writes**: operations re-check authoritatively via the same `CheckOwnership` generalization
  (`CheckScope(permission, row.Region ...)`), exactly like `own` re-checks today.

### `shared` (per-record ACLs) stays deferred — but now has a shape

`shared` is just a `where` whose attribute is a JOIN instead of a column: `row.Id ∈
share_table[actor]`. Once `where` exists, `shared` is one more declared scope
(`.SharedScope(context, permission, db.Shares)`) and a framework `shares` table
(`Entity, RecordId, GranteeAccountId | GranteeTeam`), plus share/unshare operations. Nothing about
it changes the policy model — it slots into the same suffix, union and re-check seams. Build it
when a real domain needs record-level sharing, not before.

## What this deliberately does NOT do

- **No expression language.** Predicates are `==`/`∈` against declared attributes. The moment a
  policy needs `region == north AND status != closed`, that is an automation-rule or a bespoke
  view, not a data scope.
- **No attribute hierarchy.** `region` values are opaque strings; if regions ever nest, model them
  as tenants (that hierarchy already exists and cascades).
- **No account-level attributes.** Facts that are true of the person across tenants (locale,
  notification prefs) are profile data, not authorization data.

## Decisions to settle

- **D-AA1 — attribute storage on the membership: JSON map, string | string[] values only.**
  (Proposed above; alternative: a normalized attribute table — more relational purity, zero
  practical gain at this cardinality.)
- **D-AA2 — names declared by resources via `AttributeScope`, validated at `policies.define`.**
  (Alternative: free-form names checked only at enforcement — rejected: silent no-ops on typos.)
- **D-AA3 — grant encoding `:where:attr=value` baked at actor resolution.** (Alternative: resolve
  attributes at QUERY time from the membership — one less encoding, but the actor's grants stop
  being self-contained and every scope helper needs a DB hook.)
- **D-AA4 — missing attribute ⇒ zero rows (fail closed).** (Alternative: fall back to `own` —
  friendlier, but a data-scope control that silently widens or narrows on a missing fact is the
  exact fail-open class the review round killed.)
- **D-AA5 — `shared` stays deferred until a domain needs it; it will be a declared join-scope on
  the same seams.**
