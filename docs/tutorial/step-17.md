# Step 17 — Invoicing arrives as a plugin — and Orders still doesn't know *(BUILT — [31-cross-domain-plugins.md](../31-cross-domain-plugins.md), `samples/invoicing`)*

Step 13's plugin lived beside the domain; Step 16's gated it. Step 17 is the vendor plugin that
becomes part of another domain's DAILY SURFACE — invoicing inside Orders — and the bar does not
move: no host CLR type anywhere in the plugin, no host edit beyond the one-line storage opt-in,
everything per-tenant activated and entitlement-priced.

1. **The vendor's aggregate.** `Invoice` carries a wire-key `Guid OrderId` (no navigation, no
   FK) and a DENORMALIZED `OrderNumber` — cross-boundary joins don't exist (D-X3), so the
   number is copied at the moment it is known. Host opt-in: `AddInvoicing(modelBuilder)`.
2. **Its own vertical.** `invoicing.create-from-order` / `finalize` / `mark-paid`, an invoice
   list whose Query takes `Guid? OrderId` (the mechanical filter), embedded sv/en locales, a
   nav contribution. Nothing new — Step 13 machinery.
3. **Compatibility, stated at build time.** `plugin.RequiresView("orders.detail", "id",
   "number", "estimatedTotal")` — PLG008 fails the BUILD if the host doesn't expose exactly
   that, and the create operation reads the order through the ACTOR-mode `IHostViewReader`:
   permission-checked like the wire, so a user who may not see an order cannot invoice it.
4. **The draft writes itself — against a contract, not folklore.** An
   `OnEffect("order-completed")` subscriber drafts the invoice post-commit — idempotent under
   redelivery, number from the payload, amount backfilled through a SERVICE-MODE declared read
   (no actor exists in the outbox; the readable surface is exactly the RequiresView list,
   never a superuser). The payload shape is a build-time fact (D-X5): the host declares
   `.PublishesEvent("order-completed", "orderId", "number")` in its model, the plugin declares
   `plugin.RequiresEvent("order-completed", "orderId", "number")`, and PLG009 fails the BUILD
   on a subscription to an unknown event or a required field the publisher doesn't carry. The
   manifest gains an `events` section — `"order-completed": { "fields": ["orderId","number"],
   "subscribedBy": ["inspect","invoicing"] }` — so `SubscribedBy` shows in the impact report,
   symmetric with `GatedBy`.
5. **Invoicing pushes back.** A gate on `orders.complete`: an order with a PENDING DRAFT
   invoice cannot complete. The gate reads the plugin's own table off the wire input; the
   manifest shows `orders.complete → gatedBy: ["invoicing"]`.
6. **The order wears its invoice status.** `plugin.ExtensionField("order", "invoiceStatus",
   "selection", options: draft|invoiced|paid, readOnly: true)` — and the missing write half,
   D-X2's `IPackagedFieldWriter`: structurally scoped (only this plugin's declared keys),
   semantically validated, audited with the PLUGIN as the attributed actor, live-refreshing
   open grids. `readOnly` marks plugin-owned state: the field renders in grids and filters
   like any extension field but is EXCLUDED from forms, and the wire extension channel rejects
   writes (`extensions.read-only-field`) — the status shows everywhere, and its state machine
   stays the plugin's. Tenant-defined fields are never read-only. The column and filter appear
   on the host's orders grid with zero host changes.
7. **"Create invoice" where the user lives.** D-X1: `plugin.GridAction("web.orders.list",
   "invoicing.create-from-order", bind => bind.Field("orderId", fromColumn: "id"))` — a row
   action ON THE HOST'S GRID, declared bind instead of name-convention, PLG006-verified,
   rendered only for entitled+activated tenants and permitted users. It composes with the
   subtree grid for free: the button on a child company's row acts in that company.
8. **The tenant clicks Activate.** Entitlement-gated `plugins.activate`; before it, the
   operation, the field, the button and the nav page do not exist for that tenant — verified
   both ways on the wire, including a sibling tenant that never activates.
9. **What we didn't need.** Host code names the plugin nowhere; the capability manifest is
   DERIVED from the model: *reads your orders (id, number, estimatedTotal); adds field
   order.invoiceStatus; gates orders.complete; adds an action to your orders list; subscribes
   to order-completed.* The record-bound detail panel is one host line —
   `model.Slot("web.orders.detail", slot => slot.Key("orderId"))` plus one `.Slot(…)` section on
   the declared orders page (Step 18 — there is no modal React to edit) — and the invoice grid
   lands in it unnamed (D-X4); the accounting-provider push is Step 10's outbound seam applied,
   not new machinery.

The whole scenario — activate, create from the grid, duplicate rejected, ghost order rejected
by the declared read, status on the order row, plugin-attributed audit, complete blocked by the
draft, finalize, complete passes, auto-draft on a bare completion, paid rippling back onto the
order — runs as a 26-check wire suite against `samples/invoicing`. `CompleteOrder` and the
orders grid declaration were not touched.

---
