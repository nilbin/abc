# 34 — The field-service domain: stress-testing Tam as a consumer

Status: **plan** (milestones marked as they land). This arc exists to answer one question:
does building real domain depth feel like using the framework or fighting it? Everything
here is written as a CONSUMER of Tam — host code in samples/erp, plugins in samples/ —
and every place the framework resists gets a line in the friction log at the bottom,
whether or not we fix it immediately.

## Why field service

Norrservice is a service company; the Orders/Customers spine already implies the rest of
the story: customers commission PROJECTS, projects break into WORK ORDERS, technicians
book TIME and consume MATERIALS against them, and the office turns approved work into
invoices. This slice is deep enough to exercise every axis the framework claims —
ownership, hierarchy, state machines, derivations, cross-domain plugins — without
inventing a domain the sample's tenants wouldn't recognize.

## The slice

```
Project        customer-linked container: number, name, status, budget (money)
WorkOrder      project-linked unit of work: assignment, scheduling, status machine
               (draft → scheduled → in-progress → done → closed), location
TimeEntry      technician-owned booking on a work order: date, hours, rate, note
MaterialLine   stock consumption on a work order: item, quantity, unit price
StockItem      the small catalog MaterialLine references: sku, name, unit, price
```

## What each piece is chosen to exercise

- **Project**: subtree grids over the tenant tree (group-wide project list), money
  fields, a customer LookupSelect, a declared page with record surface.
- **WorkOrder**: the paired-atom OWN scope for real (technicians see their own; the
  `work-orders.read` / `read-all` pair), EDIT001 intent operations for the status
  machine (schedule/start/complete/close — never a Change<T>), assignment (docs/28),
  date scheduling, a tenant custom field defined at runtime (docs/15 re-verified on a
  NEW entity, not the one the tutorial always uses).
- **TimeEntry**: ownership by the booking technician, derivations (amount = hours ×
  rate live in the form), an approvals-plugin gate on `time.approve` (the docs/31
  wildcard-gate story hitting a domain it was never written for).
- **MaterialLine + StockItem**: reference fields between NEW aggregates, quantity ×
  price derivation, grid row actions, seed data.
- **Invoicing plugin (existing)**: extended to draft invoices from approved time +
  materials of a completed work order — a cross-domain plugin consuming aggregates that
  did not exist when the plugin was written (RequiresView against the new views).
- **Nav/pages**: a new `field` work mode for technicians (nav v2 override registry gets
  a real tenant story: an office tenant hides it), declared pages for every aggregate —
  the registerPage count must STAY zero.
- **Wire + RLS**: everything verified on SQLite AND Postgres with RLS provisioned; the
  read-set scaling question (docs/33 deferral) gets measured, not guessed.

## Rules of engagement

1. Consumer discipline: samples/erp and plugins only. A framework change is allowed
   ONLY when the friction log shows the consumer path is impossible or dishonest —
   and then it ships as its own commit with the log entry as its rationale.
2. Every milestone lands with tests, wire verification, STATUS.md, and — where a
   decision emerged — a docs/19 ledger entry. Same bar as framework work.
3. The friction log below is append-only and honest: "this took three tries", "the
   error pointed nowhere", "I wanted an analyzer here" are all entries.

## Milestones

- [x] M1 — Project + StockItem: entities, CRUD operations/views, declared pages, nav,
      locales, seed. (The boring baseline: how fast is a plain aggregate?) **BUILT** with
      ZERO framework changes and zero React: two aggregates (Project deepened with
      number/status/budget + close/reopen intents with a cross-aggregate open-orders
      guard; StockItem catalog with retire-don't-delete), 7 operations, 4 views, 2
      declared pages, subtree-capable projects list, per-role visibility. Verified: 21
      wire checks + full 12-suite matrix on SQLite AND Postgres (RLS policies confirmed
      on both new tables). Three friction entries below came out of it.
- [ ] M2 — WorkOrder: status machine, assignment, own-scope paired atoms, scheduling,
      custom field on a new entity.
- [ ] M3 — TimeEntry + MaterialLine: ownership, derivations, approvals gate on
      time.approve, row actions.
- [ ] M4 — Invoicing extension: draft invoice from a completed work order's approved
      time + materials; work-mode nav for technicians incl. a tenant nav override.
- [ ] M5 — Postgres + RLS pass over the whole slice; read-set scaling measured;
      friction-log triage → fixes or explicit deferrals.

## Friction log

(append entries as they happen; never delete)

- `decimal` maps to the Money semantic type by TYPE-NAME sniffing
  (`SemanticTypes.For`: `t.Name.Contains("Money")`) — `HourlyRate`/`UnitPrice` will
  silently render as bare numbers. Predicted before M1; candidate fix is a `[Format("money")]`
  attribute or a Money value wrapper in the closed vocabulary.
- (M1, confirmed) `Budget` and `UnitPrice` hit exactly that: both are money, both typed
  as bare "number"; the money RENDERER had to be attached by hand per form field and the
  grid columns show raw numbers. The semantic vocabulary has Money — the CLR mapping just
  can't be told to use it without renaming the member.
- (M1) The flat label namespace collides across aggregates: `Project.Number` silently
  reused `labels.number` — which orders had already claimed as "Order number". Nothing
  warns; the wrong label only shows in the UI. Escape was a type-level [LabelKey] on the
  ProjectNumber wrapper (nice once you know it). Candidate: L10N-family warning when a
  convention-derived key is shared by members of different aggregates.
- (M1) `Change<T>` on the raw wire is `{original, value}` — a consumer writing JSON by
  hand (agent, integration) who guesses `{value}` or `{value, base}` gets
  `concurrency.field-conflict` with empty args and no hint that the ORIGINAL was missing
  rather than stale. The form runtime hides this entirely; the finding could say which.
- (M1, positive) The full slice — 2 aggregates, 7 ops, 4 views, 2 pages, nav, sv+en,
  seed, 21 wire checks — was one sitting with no framework edits; L10N001 caught every
  missing key at build; PAGE001 verified the record surfaces; conventions (record IS the
  form, grid defaults) meant the stock page needed no configure at all.
