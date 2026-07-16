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
- [x] M2 — WorkOrder: status machine, assignment, own-scope paired atoms, scheduling,
      custom field on a new entity. **BUILT**, again zero framework changes: the 5-state
      machine (Draft → Scheduled → InProgress → Done → Closed) is entity methods behind 7
      intent operations; scheduling assigns AND dates in one intent (resolved against the
      framework's membership table, display name SNAPSHOT onto the entity); start/complete
      are own-scoped with -all pairs (technician runs her own orders end to end, dispatcher
      works the board); editing locks once work starts; completion publishes
      work-order-completed (the M4 invoicing seam). The runtime custom field
      (requiresLift, boolean) landed on the new entity exactly as docs/15 promises —
      seeded like an admin would, rides list rows/forms/audit untouched. Verified: 18-check
      fieldm2 suite (incl. the resolve endpoint serving assignee options from a derivation)
      + full 13-suite matrix on SQLite AND Postgres (RLS on WorkOrders confirmed).
- [x] M3 — TimeEntry + MaterialLine: ownership, derivations, approvals gate on
      time.approve, row actions. **BUILT — and built the way this arc was meant to run**:
      by an agent restricted to the DOCS and the samples (framework source forbidden,
      compiler errors as IntelliSense). It shipped the whole slice with zero framework
      changes — technician-owned time (paired atoms + [Widens]), amount/rate snapshot
      semantics (a material line keeps its entry-time price when the catalog moves),
      time.approve as an intent, three derivations (live hours×rate, latest-own-rate
      default, stock-item options), two read-only-record declared pages, row-action
      prefill from the work-orders grid — and filed 7 doc gaps + 6 DX frictions (below
      and in the milestone commit). Verified independently after the fact: 21-check
      fieldm3 suite INCLUDING the approvals-plugin gate parking time.approve via a
      tenant-defined rule (the wildcard gate over a domain that postdates the plugin),
      full 14-suite matrix on SQLite AND Postgres, RLS confirmed on TimeEntries and
      MaterialLines.
- [x] M4 — Invoicing extension: draft invoice from a completed work order's approved
      time + materials; work-mode nav for technicians incl. a tenant nav override. **BUILT**:
      the invoicing plugin subscribes to work-order-completed and drafts from what the work
      actually COST — approved time entries plus material lines, read through service-mode
      declared reads over the M3 views (RequiresView time.list/materials.list, filtered by
      the number the payload carries; draft time excluded, proven by the amount math on the
      wire: 2000 + 178 = 2178 with a 5000 draft entry ignored). The aggregates postdate the
      plugin; only its CONTRACT grew. Invoice gained a nullable WorkOrderId (exactly one
      source set) and the "order number" label honestly became "source document". The host
      added the technician "field" MODE (my-work/my-time over the same declared pages), and
      the wire suite hides it via nav.override and restores it via nav.retire — the docs/30
      v2 story on a mode that exists for exactly this purpose. In sequence, fieldm4 also
      walks the park → release → replay loop when fieldm3's approval rule gates
      time.approve. Verified: 14-check fieldm4 + full 15-suite matrix on SQLite AND
      Postgres.
- [~] M5 — Postgres + RLS pass over the whole slice (CONTINUOUSLY DONE — every milestone
      ran the full matrix on Postgres with RLS provisioned); read-set scaling MEASURED
      (docs/33: the GUC-array policy costs rows × |read set| — 240 ms for a 200-node
      subtree over 20k rows even paged; a registry semi-join answers the same question in
      0.2 ms — candidate policy change recorded, not yet built). Remaining: the friction-log
      triage → fixes or explicit deferrals (decision pending).

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
- (M2) There is NO framework story for rendering an ACTOR reference in a view. Joining
  the account table from a domain view means a string↔Guid cross-provider join
  (AssignedToActorId is a string, account ids are Guids — SQLite renders Guids as
  upper-case text, Postgres lower-case; the join silently drops rows on one provider).
  Denormalizing the display name onto the entity at assignment time is the honest
  workaround, but every domain with assignees will re-invent it. Candidate: an actor
  reference semantic type the view layer knows how to render.
- (M2) The second aggregate that needed a PICKER had nowhere to go: `ProjectId` on the
  work-order create form is a raw guid input, and the schedule form's assignee options
  had to ride a ServerDerivation keyed on [DependsOn(WorkOrderId)] — a trigger chosen
  because SOMETHING must fire, not because the data depends on it. CustomerPicker solved
  this for customers with bespoke React. A declarative `lookup(view)` renderer — field
  options served from any lookup view with search — is now the arc's clearest missing
  framework piece; it would delete the derivation AND the bespoke picker.
- (M4) Approval RULES cannot be retired: approvals.rules.define exists, approvals.rules.
  retire does not — a tenant that gates an operation can never un-gate it, and the wire
  suites had to design AROUND a permanent rule (fieldm4 walks the release loop instead).
  The "retire, don't delete" convention needs its missing half here.
- (from review) A slot appears TWICE in the composition root and the duplication reads as
  a bug until explained: `model.Slot("web.orders.detail", s => s.Key("orderId"))` DECLARES
  the contribution point (the id and record context plugins target — it exists even when
  the host renders custom React), while `record.Slot("web.orders.detail")` PLACES it in
  the declared page's layout. Two different questions, but the create form doesn't make a
  reader ask them. Candidate: page-placed slots auto-declare (inheriting the record's
  key), keeping standalone `model.Slot` only for `external: true` slots placed in custom
  React — one call in the common case.
- (M3) A form has no seat for a COMPUTED display value: the live amount (hours × rate)
  had to become an OPTIONAL OPERATION INPUT (`BookTime.Input.Amount`) that exists only so
  the derivation's Suggest has a field to target — the server ignores it and recomputes.
  docs/05 lists "calculated transient values" and a `DerivedValue`/`DefaultOnceFrom`/
  `DerivedFrom` vocabulary, but only `Suggest`/`AddOptions`/`AddWarning`/`From` are built
  (the compiler confirms: no `Derive` on DerivationResult). Candidate: a read-only
  form-level computed field, or build the derived-value channel docs/05 already designs.
- (M3) The `/api/forms/{id}/resolve` REQUEST shape is documented nowhere: posting the raw
  input (as the MCP tool does per step-08) is a 500 with a bare JsonSerializer stack trace;
  the body must be `{"input": {...}}`. Found by trial. Step-08/docs/05 should show the HTTP
  shape, and the endpoint should 400 with a hint instead of 500.
- (M3) The "SOMETHING must fire" derivation trigger (M2 entry) is now a PATTERN, copied
  twice more: time.book's rate default and materials.add's stock-item options both key on
  `[DependsOn(WorkOrderId)]` solely because the row action prefills it when the form opens.
  Three sites now want "fire on form open" / a declarative lookup renderer.
- (M3, positive) The paired-atom own scope generalized to a second domain in one line per
  seam: `ScopedUnless` on the two time views + granting technicians only base atoms made
  "Tekla sees her own time, the office sees the board, viewer levels expand to read-all"
  all fall out — verified on the wire with three differently-scoped tokens and zero
  authorization code in the feature. The snapshot discipline (technician name, unit price)
  was likewise a constructor argument each, not a framework fight. five entity methods
  returning findings, seven thin operations, zero pipeline awareness. Own-scope pairs
  and TAM006 made "technician runs her own order end to end, dispatcher works the board"
  fall out of role composition — the wire suite proved both boundaries (403s included)
  without any authorization code in the domain.
