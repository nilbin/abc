# 24 — Subscriptions, Seats & Entitlements

**Status: designed; a minimal enforcement core is implemented** (subscription registry, plan-based plugin entitlements gating `plugins.activate`, seat limits gating `users.define` — see STATUS.md). Billing-provider integration and usage metering are designed, not built.

## The idea

A framework that lets a marketplace sell plugins (docs/22, D8) needs a way for those plugins — and the base product, and seats — to **cost money**. The design keeps faith with the rest of Tam: entitlements are *data* (a tenant's subscription, like its roles and custom fields), enforced *mechanically* in the pipeline (like authorization and gates), and the actual money movement lives *outside the process* on the integration channel (like every other external system). No payment code in the monolith.

Three things a subscription governs, each already a first-class concept the subscription just bounds:

1. **Plan** — the tenant's tier (`free` / `standard` / `enterprise`). A named bundle of entitlements, exactly the shape of a role but for capabilities instead of permissions.
2. **Seats** — how many active users the tenant may have. Users are already tenant data (auth, docs); seats put a ceiling on them.
3. **Plugin entitlements** — which marketplace plugins the plan includes. This composes with per-tenant activation (D8): a plugin must be *both* entitled by the plan *and* activated by the admin to appear.

## The model

```
subscriptions   TenantId (pk), Plan, Seats, PluginEntitlements (json), Status, RenewsAtIso
```

One row per tenant. `Status` ∈ {`trialing`, `active`, `past_due`, `canceled`} — the lifecycle a billing provider drives. `PluginEntitlements` is the set of plugin ids the plan includes (`["*"]` = all). The subscription is managed through framework operations like everything else:

- `subscriptions.set-plan` (permission `subscriptions.manage`, typically held only by a billing webhook's service actor, not tenant admins) — sets plan, seats, entitlements, status. Idempotent, audited.
- `subscriptions.current` view — the tenant's own plan/seats/usage, readable by admins so the UI can show "4 of 5 seats used, upgrade for more".

**Why a service actor, not the admin:** the tenant admin activates plugins and invites users (self-service); they do **not** edit their own plan — that's the billing provider's job, delivered over the integration channel. `subscriptions.set-plan` is just the inbound edge of that integration, using the same inbox/idempotency machinery as any other partner sync (docs/10). A Stripe `customer.subscription.updated` webhook maps to one `subscriptions.set-plan` call; nothing bespoke.

## Enforcement — mechanical, in the pipeline

Two chokepoints, both already the right place:

- **Plugin activation** (`plugins.activate`): before writing the activation row, check the tenant's subscription entitles the plugin. Not entitled → `subscriptions.not-entitled` finding (a localized upsell, not a crash). This is the entitlement guard D8's "per-plugin billing tier" reserved.
- **Seat limit** (`users.define`, only when creating a *new active* user): count active users against `Seats`; over the limit → `subscriptions.seat-limit` finding. Reactivating or editing an existing user doesn't consume a new seat.

Both read the subscription row once (cheap, one-per-tenant), and both degrade safely: a tenant with **no** subscription row on its chain falls to the host-configured default (`Subscriptions.Defaults`, shipped as plan `unconfigured`, 2 seats, no entitlements) — the framework is fully usable without a billing system wired up, which keeps the OSS/self-hosted story clean.

**Seats vs the tenant hierarchy (docs/26–27).** Seats count memberships at their **attachment node**. A *cascading* membership (docs/27) attached at a region therefore consumes **one seat at the region** while reaching every descendant company — one region admin over 50 companies is 1 seat, not 50. This is acknowledged and accepted: cascade is an authorization construct, and pricing it per-reach would tax exactly the roll-up roles the hierarchy exists for. If a commercial plan ever needs reach-based pricing, that is a docs/24 counting change (e.g. count a cascading membership against each descendant's ceiling, or price cascading seats differently) — the authorization model doesn't move.

Because entitlement is enforced at activation, not at every request, an entitled-then-downgraded tenant keeps working until the next activation attempt; a background reconciliation (deactivate plugins a lapsed plan no longer entitles) is the `past_due`/`canceled` handler — designed, on the integration channel, not yet built.

## Hierarchy — the anchor model (BUILT)

docs/24 was written flat; docs/26 then made tenants trees. Unreconciled, the flat model gave
three wrong answers in a tree: a child with no subscription row was a default-plan *island inside a
paid group* (entitled to nothing its parent paid for); every new child minted its own default seat
pool (a seat-ceiling bypass one `tenants.create` wide); and nothing said whether a subsidiary
could be billed separately. The organizing principle mirrors the settled authorization rule —
*capability cascades, data does not* (docs/26): **a subscription is the money above a subtree,
and it cascades; activation is a per-node choice, and it does not.**

A `SubscriptionEntity` row **is an anchor**: it covers its own node and every descendant, until a
nearer anchor shadows it. No schema change — anchorship is implicit in which node has a row.

- **D-S1 — nearest ancestor-or-self anchor governs** (`Subscriptions.CoveringAsync`): the same
  materialized-path chain walk actor grants use. The unconfigured default survives only when NO anchor
  exists on the chain — and it is then anchored at the **root**: one tree, one commercial
  standing; child nodes never mint fresh default seats.
- **D-S2 — entitlement is the anchor's; activation stays per node; entitlement is enough.** A
  child activates a plugin iff the covering anchor entitles it. No per-subtree allow/deny masks —
  "the parent didn't intend it" is governance, answered by who holds `plugins.manage` where
  (deny rules stay settled out, docs/27 D-A4).
- **D-S3 — seats pool at the anchor**: the count spans the anchor's covered set (its subtree
  minus sub-anchored subtrees) and the seat LEASE lands on the anchor's row — invites racing at
  two different covered nodes now conflict at SaveChanges. A materialized default lands at
  the root, never at a child (a child row would silently shadow a future root plan). An
  over-ceiling pool blocks only NEW consumption; existing members are never deactivated.
- **D-S4 — sub-anchors are the deliberate exception** for genuinely separate billing (an
  acquired subsidiary on its own contract): `subscriptions.set-plan` run while acting at that
  node. Creation is billing-provider-only *by construction* — `subscriptions.manage` is a
  reserved atom (excluded from `*`, ungrantable through roles). Boundaries are absolute: a
  sub-anchored subtree ignores every anchor above it — no entitlement unions, no seat borrowing
  (union semantics would open pricing arbitrage).
- **D-S5 — `tenants.move` across anchor boundaries: allow, warn, never destroy.** The move is
  structural and succeeds (the mover typically cannot fix billing anyway — the atom is
  reserved); it returns `subscriptions.entitlement-lost` / `subscriptions.seat-overflow`
  WARNINGS. Enforcement rides the existing downgrade semantics: no new activation of an
  unentitled plugin, no new seat past an overflowed pool, and the S2 reconciliation job
  deactivates later — undo the move before it runs and nothing happened. (Hard-failing the move
  or deleting activation rows were both rejected: the first holds re-orgs hostage to billing,
  the second destroys undoable state.)
- **D-S6 — wire impact is two additive facts**: `subscriptions.current` shows the COVERING
  subscription plus `anchorTenantId` and the pooled `seatsUsed`. The entity and `set-plan` are
  unchanged (set-plan at a non-root node *is* sub-anchor creation).

The unconfigured default, stated precisely: it is NOT a product tier — it is the enforcement answer to
"what do the gates do when billing is silent". Unlimited would make ignoring billing a bypass
(fail-open); zero would make the framework unusable without a billing provider (no first user).
The shipped baseline is bootstrap-sized and host-configurable: `Subscriptions.Defaults`, with
`SubscriptionDefaults.Unlimited` for self-hosted deployments that have no vendor to bypass.

**The signup recipe.** In a product that sells subscriptions, the default should never govern a
real customer: signup is host code, and it ends by writing the row — create the root tenant,
then `subscriptions.set-plan` with the purchased plan or the trial policy (the erp sample's seed
does exactly this: `demo` carries an explicit `standard` row, which is why the wire tests
resolve a real plan, not the default). The default is the *backstop* for the states the
framework cannot rule out — a signup bug, an imported tree, a dev box — and it is deliberately
restrictive so a missing row surfaces as a seat ceiling within days instead of a silent
entitlement leak. A deployment that doesn't do billing sets `Unlimited` once and the whole
subsystem disappears from view; a deployment that does leaves the backstop tight and treats any
tenant reporting plan `unconfigured` as a provisioning bug.

Deferred: `subscriptions.detach` (removing a sub-anchor when a subsidiary is re-absorbed into
group billing) — deliberately unbuilt until the scenario is real.

## Marketplace composition (docs/22 tiers × this)

The marketplace's three tiers each get their price hook here:

| Tier | Priced by | Mechanism |
| --- | --- | --- |
| Configurations (tenant packages) | usually free / plan-gated | a package install could check an entitlement the same way |
| Vetted plugins | per-plugin or plan-bundled | `PluginEntitlements` gates activation |
| External apps | the app's own billing | out of scope — the app bills the tenant directly |

A plugin author's revenue model becomes: the marketplace lists the plugin against a price; a purchase makes the billing provider add the plugin id to the tenant's `PluginEntitlements` via `subscriptions.set-plan`; activation now succeeds. The framework never touches money — it reads one boolean.

## What stays out (v1 ceilings)

- **No metering / usage-based billing** (per-operation, per-API-call). The audit trail is the natural meter (every operation is a row), and a metered-billing export over the outbox is the design, but counting and rating are a billing-provider concern.
- **No proration, invoices, dunning, tax** — all provider responsibilities; Tam stores only the resolved entitlement state.
- **No in-app checkout** — payment capture is the provider's hosted flow; the app deep-links to it. (Collecting card details in-process would be the exact "never publish a credentialed flow" line the framework shouldn't cross.)
- **Seats count active users only**; team/org sub-structures (seats per department) would extend the model the way D1's scopes extend permissions.

## Phasing

- **S1 (implemented)**: subscription registry, `subscriptions.set-plan` / `subscriptions.current`, plugin-entitlement gate on activation, seat gate on user creation, host-configurable unconfigured default, sample seeded with a `standard` plan. Localized upsell findings.
- **S2**: billing-provider integration (Stripe-shaped) over the inbox — webhook → `set-plan`; a hosted-checkout deep link; the `past_due`/`canceled` reconciliation background job.
- **S3**: metered export over the outbox; per-department seat pools.
