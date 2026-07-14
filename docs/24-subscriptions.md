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

Both read the subscription row once (cheap, one-per-tenant), and both degrade safely: a tenant with **no** subscription row is treated as the `free` plan with a default seat count and no plugin entitlements — the framework is fully usable without a billing system wired up, which keeps the OSS/self-hosted story clean.

**Seats vs the tenant hierarchy (docs/26–27).** Seats count memberships at their **attachment node**. A *cascading* membership (docs/27) attached at a region therefore consumes **one seat at the region** while reaching every descendant company — one region admin over 50 companies is 1 seat, not 50. This is acknowledged and accepted: cascade is an authorization construct, and pricing it per-reach would tax exactly the roll-up roles the hierarchy exists for. If a commercial plan ever needs reach-based pricing, that is a docs/24 counting change (e.g. count a cascading membership against each descendant's ceiling, or price cascading seats differently) — the authorization model doesn't move.

Because entitlement is enforced at activation, not at every request, an entitled-then-downgraded tenant keeps working until the next activation attempt; a background reconciliation (deactivate plugins a lapsed plan no longer entitles) is the `past_due`/`canceled` handler — designed, on the integration channel, not yet built.

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

- **S1 (implemented)**: subscription registry, `subscriptions.set-plan` / `subscriptions.current`, plugin-entitlement gate on activation, seat gate on user creation, free-plan default, sample seeded with a `standard` plan. Localized upsell findings.
- **S2**: billing-provider integration (Stripe-shaped) over the inbox — webhook → `set-plan`; a hosted-checkout deep link; the `past_due`/`canceled` reconciliation background job.
- **S3**: metered export over the outbox; per-department seat pools.
