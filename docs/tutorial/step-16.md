# Step 16 — Approvals arrive as a plugin — and the domains never notice *(BUILT — the seams and the package, `samples/approvals`)*

Norrservice's group buys an add-on from a workflow vendor: purchase approvals. Orders above a
threshold need a manager's sign-off; time corrections need the team lead. The point of this step
is what it does **not** require: no change to `CreateOrder`, no change to any domain, and no
approval engine in the framework. Groups and workflows are exactly the things docs/28 keeps out
of core — so they arrive the way inspection checklists did in Step 13: as a package the tenant
activates.

What the vendor ships — `samples/approvals`, ~400 lines all told: its OWN aggregates in the host
database like inspect's checklists (`ApprovalGroup` — nested, and nesting semantics are the
PLUGIN's problem, the framework never learns about groups — `ApprovalGroupMember`,
`ApprovalRule` over host *wire* operation ids with optional thresholds, `ApprovalRequest` — the
parked envelope keyed by its payload hash); `approvals.*` operations and views (rules and group
admin, request list, approve/reject) — each an ordinary operation: authorized, audited,
localized, in the manifest, an MCP tool; `OnEffect("approvals.requested")` mailing the effective
approver set through `ITamEmail`; and ONE wildcard gate.

The interesting part is that gate. Step 13's gate was declared against one known operation id;
approvals intercepts operation ids *the tenant configures at runtime*, parks the request, and
later runs it for real. Walking through one order, exactly as the wire verification replays it:

1. Didrik submits `orders.create` for 180 000 kr. The approvals gate (running inside the
   pipeline, before the handler's effects commit) consults its `ApprovalRule` table: this
   operation + this threshold ⇒ sign-off required. The gate **parks the envelope** — operation
   id, wire body, payload hash, actor, culture — as an `ApprovalRequest` via
   `gate.Park<ParkEnvelope, ApprovalRequest>(…)` (the domain transaction rolls back; the
   envelope commits from a fresh scope; an identical resubmit re-blocks but dedupes on the
   hash), and blocks with `approvals.pending` (a localized finding the form
   renders as "submitted for approval", not as an error).
2. The rule's group resolves — members of the group plus every nested subgroup —
   `OnEffect` mails the approvers, and the pending request sits in the plugin's grid.
3. A lead runs `approvals.approve` (four-eyes: never the initiator; membership checked through
   the nesting). On commit, the plugin **replays the parked envelope** through the real
   pipeline — the same executor every caller uses — as the *original* actor with grants
   re-resolved as of now, marked `InvocationSource.Workflow` (the gate's pass condition, settable
   only by compiled code). The order is created; the audit trail shows both facts: the
   `orders.create` entry reads actor Didrik / source Workflow / correlation = the request id,
   and the `approvals.approve` entry names the lead who released it.

The domain wrote none of this. `CreateOrder` still doesn't know approvals exist — for tenants
without the plugin, nothing changed; for tenants with it, the manifest says `orders.create` is
gated and the impact report shows it, exactly like Step 13.

**What this scenario proves — and the three seams it demanded.** This is the sharpest stress
test of the plugin architecture so far. The three gaps it exposed are now framework seams, each
proven end to end through the real pipeline in the test suite:

1. **Config-driven gate targets — built.** A gate registered as `[GateAll]` (or `plugin.GateAll<MyGate>()`)
   runs on EVERY operation (after the operation-specific gates) and receives `gate.OperationId`,
   so which operations it actually blocks is a lookup in the plugin's own `ApprovalRule` rows —
   tenant data, not compile time. Every other gate contract is unchanged: wire input only,
   activation-gated, inside the transaction.
2. **Parking survives the rollback — built.** A blocking gate calls
   `gate.Park<MyParkedWork, TState>(state)`; the pipeline rolls the domain transaction back
   FIRST, then CONSTRUCTS the parked-work class in a fresh service scope pinned to the same
   tenant — so its injected `ITamDb` is a fresh context *by construction*, and the rolled-back
   gate scope is structurally unreachable (state crosses only as the explicit value). Work
   parked by a gate that ends up *allowing* the operation is discarded, so nothing leaks from
   attempts that went through. The `gate.PayloadHash` (the pipeline's idempotency hash) is the
   natural envelope key.
3. **Sanctioned envelope replay — built.** `EnvelopeReplay.ReplayAsync` re-executes a stored
   envelope through the FULL pipeline — authorization, validation, rules, gates, audit — as the
   ORIGINAL initiator, whose grants are re-resolved *as of now* (a revoked role or deactivated
   account fails the replay closed; approval releases a block, it never escalates). The run is
   marked `InvocationSource.Workflow` — the parking gate lets it pass while every other gate
   still runs — and the envelope id rides `CorrelationId` into the audit entry and doubles as
   the initiator-scoped idempotency key, so a redelivered approval replays the stored outcome
   instead of executing twice. Dual attribution falls out: the operation's audit names the
   initiator and correlates to the envelope, whose own trail names the releaser.

With the seams in place, "an opinionated nested-group approval system for existing domains,
without the domains knowing" stopped being a slogan: `samples/approvals` is that package, and
the whole scenario above — activate, configure, block, park, notify, approve through a nested
group, replay, reject, dedupe — is verified on the wire against the running sample. `CreateOrder`
was not touched. That is the bar docs/22 set for the plugin architecture, met.


---
