---
name: domain-design
description: Use BEFORE adding a new domain, aggregate, entity, or owned-child relationship anywhere in samples/*/Domain or a framework package — or when an operation is about to create/mutate rows of a child entity directly. Forces the aggregate design pass (roots, owned children, invariants, intents) so domains are designed, never fast-forwarded.
---

# Domain design pass — run BEFORE writing the entity

Two DDD retrofits (#108 anemic entities, #110 free-floating checklist items) happened
because domains were fast-forwarded: entities written as data bags first, invariants
bolted into operations, ownership discovered later by review. This pass front-loads the
thinking. Write the answers down (task description or commit message) BEFORE code.

## 1. Classify every entity

For each entity in the new domain, decide which of exactly three shapes it is:

- **Aggregate ROOT** — has identity, a lifecycle, and invariants of its own
  (Order, WorkOrder, Checklist, ChecklistTemplate).
- **OWNED CHILD** — no identity or lifecycle outside its parent (a template line, a
  checklist line). It lives ON the root: collection navigation + backing field, root
  intents (`AddItem`, `Check(itemId)`), `internal` factory/mutators. An operation never
  writes a child through `Db.Set<Child>()` — it loads the root `.Include(Items)` and
  calls an intent.
- **PLAIN ROW** — join/lookup/config referencing EXTERNAL identities (group member →
  actor id, an approval rule over wire names). Stays flat, with the visible
  `#pragma warning disable/restore TAM008` + reason comment.

The tell that classification went wrong: an invariant that mentions two entities of the
same domain ("never passed while lines are open") implemented as a query in an operation.
That invariant names an aggregate — the two entities are one aggregate, and the rule
belongs on the root.

## 2. List the invariants, then place them

For each rule, place it at the LOWEST level that can enforce it:

| Invariant scope | Lives in |
| --- | --- |
| One entity's own state walk (open→completed, no re-cancel) | guarded transition on the entity, `Result`-returning |
| Whole aggregate (root + its loaded children) | root intent over the navigation |
| Cross-aggregate / needs the database (uniqueness, existence, membership) | the operation — the ERP idiom |

"This root has no invariants" next to a status/enum property is almost always wrong —
look again before writing public setters.

## 3. The shape checklist (compiler-backed)

- Factory: private ctor + `static Create(...)`; transitions are NAMED intent methods
  (EDIT001 mirrors this on the wire); setters private — TAM008 makes mutable public
  setters under `Domain/` a build error, so if you're reaching for the pragma, first
  re-check step 1.
- Findings live beside their aggregate in `Domain/<Aggregate>.cs`; codes are locale keys.
- One aggregate per file: `Domain/<Aggregate>.cs` + `Features/<Aggregate>.cs`
  (check_structure enforces the cap and the one-wire-prefix rule).
- EF: `HasMany(x => x.Items).WithOne().HasForeignKey(...)` for owned children. GOTCHA:
  a child created on an ALREADY-TRACKED root must be `Db.Add()`ed explicitly — its
  client-set key otherwise reads as an existing row and the save fails as a phantom
  UPDATE (surfaces as concurrency.version-conflict).

## 4. Sanity questions before coding

- Who is the factory for each entity? (A checklist instantiated from a template →
  `template.Instantiate(...)` is the factory, not the subscriber.)
- Can any pair of states disagree? (item done vs checklist passed) → they must move in
  ONE intent on the root, so the disagreement is unrepresentable.
- What does retire mean here? (retire-don't-drop everywhere; un-retire semantics?)
- Which reads are hot, and do children need a denormalized key for them? (ChecklistItem
  carries OrderId for the gate's one-query read — denormalization is fine when the root
  stamps it.)
