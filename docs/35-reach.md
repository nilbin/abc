# 35 — Reach: who an object-side grant may name

Status: **v1 BUILT** (the seam: `ReachRef`/`EntityRef` types, `IReachProvider`, model
registration with REACH001, `ReachResolver` with activation gating, the three framework
providers, and the approvals plugin's `approvals.group` provider as the plugin-side proof).
First consumer: **BUILT** — the `tam.documents` package's folder ACLs (stored ReachRef rows,
effective-ACL inheritance down the folder path-tree with own-rows OVERRIDE — a child can be
locked tighter than its parent — evaluated through the resolver on read and write via one
predicate, `DocumentAccess.VisibleFolderIdsAsync`). Documents attach to records by EntityRef
(`AttachedTo`, queryable per record). The consumer has its own doc:
[36-documents.md](36-documents.md). Decisions D-R1..D-R5.

## The question the capability axis cannot answer

The authorization model (docs/27) answers the SUBJECT-side question: *may this actor run
`documents.read` at this node?* Roles grant atoms; scopes bound rows by tenancy and ownership.
But a folder that is "visible to the dispatchers and to Tekla" asks the OBJECT-side question:
*is this actor among the people THIS ROW names?* That is a share edge — and docs/28 settled
where share edges live: they are **domain data** (the framework owns neither the group fact nor
the tagged column), enforced by the domain on both read and write like any assignment table.

What recurs across such domains is not the data — it is the **vocabulary**. Every ACL-shaped
feature needs to say "these people": a specific user, everyone holding a role, everyone in the
tenant, or a people-set some plugin owns (the approvals plugin's nested approver groups). Left
alone, each domain would mint its own reference format and its own membership-check switch
statement, and plugin-owned sets could never participate at all. The REACH SEAM is that
vocabulary, once:

- a **`ReachRef`** — a canonical, storable reference to a people-set: `kind` or `kind:id`
  (`tenant`, `user:0d3f…`, `role:dispatcher`, `approvals.group:7a41…`);
- an **`IReachProvider`** per kind — the class that answers *containment* ("does the acting
  actor fall within this reach?") and *search* (picker options for an ACL editor);
- a **`ReachResolver`** the domain calls with the stored string — parse, look the kind up in
  the compiled model, gate plugin kinds on activation, construct the provider, delegate.

The framework ships providers only for the facts it owns (docs/26 identity): `user` (account
id), `role` (role name on the acting membership), `tenant` (every member). A plugin registers
kinds under its own prefix over its own tables — reach is how a plugin-owned people-set
becomes REFERENCABLE by host domains without the host learning group semantics.

## What reach deliberately is NOT

- **Not a new principal type.** Docs/28 D-AG3: groups, if ever, arrive as one more source in
  the actor-resolution union — never threaded through enforcement. Reach does not touch actor
  resolution, `Actor.Can`, the manifest permission set, or the analyzer. It is a DOMAIN-side
  membership test the domain applies inside its own predicate (`ScopedUnless` accepts a reach
  subquery as readily as an owner column), exactly like the docs/28 assignment pattern.
- **Not a grant.** Capability still gates the operation (`documents.read` first); the ACL then
  narrows rows. Union across an ACL's entries (any reach contains → within), matching D-A5;
  no deny entries, matching D-A4.
- **Not authorization data the framework interprets on its own.** The framework never
  evaluates a reach spontaneously — only when a domain asks, against the acting context. A
  stored ref is inert until a domain's read filter or write check calls the resolver.

## The contract

```csharp
public sealed record ReachRef(string Kind, string? Id);       // canonical: "kind" | "kind:id"

public interface IReachProvider                                // ctor-DI, ITamActivator-built
{
    Task<bool> ContainsAsync(ReachRef reach, OperationContext context, CancellationToken ct);
    Task<IReadOnlyList<ReachOption>> SearchAsync(string? search, OperationContext context,
        CancellationToken ct);                                 // picker options (ref + label)
}

model.ReachProvider<UserReach>("user");                        // host/framework: bare kinds
plugin.ReachProvider<GroupReach>("approvals.group");           // plugin: prefixed (REACH001)
```

- **Kinds are dot-separated slugs**; a plugin's kinds must sit under its id prefix (the PLG001
  rule applied to reach); duplicate kinds are a build error. All REACH001.
- **Containment is evaluated against the ACTING context** (`OperationContext`): the actor and
  the ambient tenant. Providers read framework facts (memberships) or their own tables through
  ordinary ctor injection — the gate/subscriber idiom (docs/22 P2), no service locator.
- **Fail closed at every seam**: malformed ref → not within; unknown kind → not within;
  kind owned by a plugin the tenant has not activated → not within (the docs/22 existence
  rule, via `ActivationCache`). A folder shared with a group survives the plugin's
  deactivation as inert data and comes back alive on re-activation — retire-don't-drop
  applied to references.
- **`role` reach is node-local** (v1): it matches the role names on the actor's membership at
  the acting node. Whether a cascaded ancestor role should count is exactly the docs/27
  cross-level name-resolution question ("a leaf's `dispatcher` is a different role from the
  region's") — deferred until a real persona forces a decision, noted in STATUS.

## EntityRef — the typed cross-entity reference

The documents domain's second vocabulary need: a document ATTACHES to a record — an order, a
work order, a customer. That reference must name the entity KIND and the row, be storable in
one column, filterable, and carryable in event payloads:

```csharp
public readonly record struct EntityRef(string EntityKey, Guid Id);   // "order:8f3c…"
```

`EntityKey` is the wire entity vocabulary that already exists — the key `AcceptsExtensions`
binds and the extension registry stores (docs/15). EntityRef makes "attached to order X" a
value with a canonical string form (`Parse`/`TryParse`/`ToString`), instead of a
per-domain (string kind, guid id) column pair convention. Consumers validate the key against
the entities they accept — the documents domain gates unknown keys at its own seam
(fail closed, like extension-channel targeting in docs/15).

## Why a seam and not a table

The alternative — a framework `shares` table (row ↔ principal) — was already rejected in
docs/28: a framework copy of domain relationships is a denormalization with a sync problem,
and the principal column would resurrect the "new principal type" that D-AG3 forbids. The
seam inverts it: domains keep their own ACL tables with their own semantics (a folder ACL has
inheritance down the folder tree; an approval chain has quorum), and the framework contributes
what it contributed for `own` — identity, the declarative check, and the discipline that the
read filter and the write check share one predicate.

## Decisions

- **D-R1 — reach is a domain-side membership test, never a principal type.** No change to
  actor resolution, the flat grant set, the manifest, or TAM00x enforcement. Union across ACL
  entries; no deny.
- **D-R2 — the framework ships exactly the kinds over facts it owns**: `user`, `role`
  (node-local), `tenant`. Everything else is plugin territory under the plugin's prefix.
- **D-R3 — activation gates resolution, not storage.** Stored refs to an inactive plugin's
  kind evaluate to "not within" and revive on re-activation; nothing is deleted or rewritten.
- **D-R4 — REACH001 at Build()**: kind grammar (dot-separated slugs), uniqueness, and the
  plugin-prefix rule are model-compile errors, mirroring PLG001.
- **D-R5 — the manifest carries nothing yet.** Reach kinds surface to the client only when the
  first ACL editor needs pickers (the documents arc) — and then as a view over
  `SearchAsync`, activation-filtered per tenant like every plugin surface, not as a static
  manifest section.
