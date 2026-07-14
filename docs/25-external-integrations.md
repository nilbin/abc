# 25 — External Integrations: Outbound, Scheduled, Event-Triggered, with a Vault

**Status: implemented and verified** (secrets vault, settings, outbound integrations triggered by event / schedule / manual, run history, the Fortnox demo — see STATUS.md). Builds on the inbound integration channel (docs/10) and the plugin system (docs/22).

## The gap this closes

Docs/10's integrations were **inbound**: an external system posts, the framework maps it to an operation. Real integrations also **reach out** — push an invoice when an order completes, poll a vendor for new records every 15 minutes, sync nightly. That needs three things the framework didn't have: a way to *trigger* outbound work (events, schedules, manual), a place to keep the *credentials and config* those calls need, and an *audit trail* of what was sent where.

All three land on machinery that already exists. Outbound triggers are the outbox (events) plus one small scheduler. Config is per-tenant data like everything else; credentials are the same but encrypted. The audit trail is one more table. And the whole thing is a plugin contribution, so an integration is a removable, per-tenant-priced capability — not host code.

## The secrets vault: roll a thin layer over the platform, not a new dependency

The recommendation, and what's built: **ASP.NET Core Data Protection**. It is already in the framework (no NuGet, no cloud SDK), gives authenticated AES with automatic key rotation, and — the reason it's the right call rather than a hand-rolled `Aes` wrapper — its *key ring* is swappable in production for Azure Key Vault or AWS KMS via `ProtectKeysWith…`, so the same calling code goes from a dev key file to HSM-backed custody without a rewrite. Rolling our own crypto would mean owning key rotation, IV management, and authenticated-encryption correctness; a heavyweight secrets NuGet (or a cloud KMS call per read) would add a dependency and latency the vault doesn't need. Data Protection is the seam that is cheap now and scales later.

Two stores, both per-tenant data:

- **Settings** (`tenant_settings`) — non-secret config: base URLs, account ids, feature flags. Readable in the clear through `settings.set` / `settings.list`.
- **Secrets** (`tenant_secrets`) — API keys, tokens, passwords. The column holds *only* Data-Protection ciphertext. `secrets.set` encrypts under the purpose string `Tam.Secrets.v1`; **there is no read-back operation** — `secrets.list` returns keys and a set/unset flag, never the value, not even masked. The plaintext is decrypted transiently, in-process, only when an integration runs, and is never logged or returned. Verified: the DB stores 155 bytes of ciphertext, not the plaintext; no view or manifest surface exposes it.

`secrets.manage` / `settings.manage` gate the operations; both are ordinary permissions a role grants.

## Outbound integrations

A plugin declares one with a **trigger** and a **handler**:

```csharp
// Fire when a host effect commits (via the outbox).
plugin.OutboundIntegration("push-completed-order",
    new EventTrigger("order-completed"), PushCompletedOrderAsync);

// Fire on a schedule the tenant configures.
plugin.OutboundIntegration("poll-orders",
    new ScheduleTrigger(), PollOrdersAsync);
```

The handler gets an `IIntegrationRunContext` — the wire-only seam, same discipline as gates and inbound maps:

```csharp
static async Task<OutboundResult> PushCompletedOrderAsync(IIntegrationRunContext run, CancellationToken ct)
{
    var baseUrl = await run.Setting("fortnox.baseUrl", ct);   // non-secret config
    var apiKey  = await run.Secret("fortnox.apiKey", ct);     // decrypted transiently
    if (baseUrl is null || apiKey is null) return OutboundResult.Failure("not-configured");

    var number = run.EventPayload?.GetProperty("number").GetString();   // the committed effect
    run.Http.DefaultRequestHeaders.TryAddWithoutValidation("Access-Token", apiKey);
    var response = await run.Http.PostAsync($"{baseUrl}/vouchers", …, ct);
    return response.IsSuccessStatusCode
        ? OutboundResult.Success($"pushed {number}")
        : OutboundResult.Failure($"http {(int)response.StatusCode}");
}
```

`Http` is a factory-managed `HttpClient` (pooled, timed out); `Setting`/`Secret` read the vault; `EventPayload` is the committed effect for event triggers; `Services` reaches the pipeline if a run needs to write results back through operations. No host CLR types leak in.

### Triggers

- **Event.** The outbox dispatcher (docs/09), which already delivers effects to plugin subscribers, additionally fires every event-triggered outbound integration whose plugin is active for the tenant — post-commit, so the external call happens if and only if the operation committed. A failed push is *recorded as a failed run*, never a crash: the external world being down can't take down dispatch.
- **Schedule.** A tenant configures cadence with `integrations.schedule` (spec `every:15m` / `every:2h` / `daily:HH:MM`, UTC). One lightweight `IntegrationScheduler` hosted service ticks each minute, runs every due, enabled schedule of an *active* plugin, records the run, and rolls next-run forward. No Quartz/Hangfire — the needs are interval + daily, so an external scheduler would be dependency for its own sake. (Cron-grade specs are a later extension of `ScheduleSpec`, which is the single place the grammar lives.)
- **Manual.** `integrations.run` fires one now — the admin's "test it" button and a way to re-drive after fixing config.

### Run history

Every run — event, schedule, or manual — writes an `integration_runs` row (integration, trigger, ok/failed, a short detail, timestamp). `integrations.runs` is the tenant's outbound audit trail, D7-filterable by integration and status. External calls are as auditable as internal operations.

## The Fortnox demo, both directions

`samples/fortnox` now spans the whole integration surface on one plugin:

- **inbound** `orders.import` — vendor orders → `orders.create` (docs/22, inbox recovery).
- **outbound event** `push-completed-order` — on `order-completed`, POST the order to Fortnox's accounting API using the tenant's base-URL setting and API-key secret. Verified end to end: completing an order pushed `{"orderNumber":"2026-01416"}` to the (mock) external endpoint and recorded an `event/ok` run.
- **outbound schedule** `poll-orders` — GET new vendor orders on the configured cadence; verified via manual run and a valid schedule (next-run computed; `nonsense` rejected; the event integration correctly refuses to be scheduled).

That one plugin is now a complete two-way vendor connector — installable, per-tenant-priced, credential-isolated — and none of it is host code.

## Does rolling our own bite? — review-round-2 hardening

The vault, scheduler and outbound runner are hand-built rather than a `Quartz`/`Hangfire`/cloud-KMS dependency. A round of review agents on code, scalability and novelty found where that *would* bite, and each is now closed in code (with tests):

- **SSRF on the tenant-supplied base URL.** An integration's destination is tenant data, and it runs under a privileged background actor — an un-guarded client is a straight shot at the cloud metadata endpoint (`169.254.169.254`) and internal services. The `tam-integrations` client now resolves the host itself and connects **only to a validated public address** (`IntegrationEgress`, blocking loopback/link-local/private/CGNAT, IPv4 and IPv6, closing the DNS-rebinding window), and **never follows redirects** (a 302 could bounce a secret-bearing request to an attacker host). Secure by default; a deployment whose real targets are internal opts in with `AllowPrivateNetwork`.
- **Wildcard was too wide.** A `"*"` grant — a tenant admin, or a plugin running as the system actor — could call `subscriptions.set-plan` and entitle itself to any plugin. `Actor.Reserved` now names permissions that `"*"` deliberately does **not** confer (`subscriptions.manage`); they must be granted explicitly, so a plugin can never re-plan the tenant that installed it.
- **Multi-node scheduler double-fire.** `NextRunIso` is now an optimistic-concurrency token: the tick **claims** a due schedule (rolls the next-run forward under the token) *before* running it, so two instances racing the same minute collide and only one wins — at-most-once per tick across the fleet, no lock table. A covering index on `(Enabled, NextRunIso)` turns the every-minute due-scan from a full table scan into an index range.
- **A hung handler must not wedge the tick.** Each scheduled run gets a per-run deadline (`CancellationTokenSource`), so one slow external call fails that run instead of stalling the once-a-minute loop.
- **Fail closed, not 500.** A malformed inbound payload is a `422`, not an unhandled 500; a well-formed-but-incomplete row maps to a validation finding; an absurd schedule spec (`every:2000000000d`) returns invalid instead of throwing `OverflowException` into the tick.
- **Per-tenant secret binding.** The vault protector chains the tenant id into the Data-Protection purpose, so a ciphertext copied into another tenant's row cannot be unprotected there.
- **Activation reads collapsed.** The plugin-activation set is read 3–4× per request (existence, gate, overlay, manifest); a request-scoped `ActivationCache` memoizes it to one query and removes the incoherency window between reads.

## Durable messaging — review-round-3 hardening

The second review pass found the outbound side had no retry at all and the **outbox** — the scheduler's structural twin — never got the claim token and dead-letter the scheduler and inbox already had. Both are the same primitive ("a durable queue with at-least-once delivery, backoff and a poison valve"), so they now share one:

- **One retry policy, both directions.** `RetryPolicy` (exponential backoff, capped, dead-letter after N) drives the inbound inbox *and* a new outbound task queue. A failed event/schedule push is enqueued as an `outbound_tasks` row and re-driven by the `IntegrationRetryDriver` with backoff; after the cap it dead-letters. The inbox drains on the next inbound call (it must run under the caller's actor, so it has no background driver) but now honours the **same backoff gate** — a rapid re-POST can't hammer a failing row — and processes a **bounded batch** per call instead of the whole failed backlog inside one partner request.
- **Outbox claim + poison valve.** The dispatcher now takes a time-boxed lease on each row under an optimistic-concurrency token (`ClaimedUntilIso`) before delivering, so N instances no longer each deliver every event (duplicate webhooks, duplicate emails). A row that keeps throwing bumps an attempt count and **dead-letters** after a cap instead of blocking every newer event behind it forever; a lapsed lease lets another instance re-deliver, preserving at-least-once. Composite index `(DispatchedAtIso, DeadAtIso, CreatedAtIso)` supports the poll.
- **Event pushes are time-boxed and isolated.** Each event-triggered outbound run gets the same per-run deadline the scheduler uses, so a slow endpoint can't stall the dispatch loop; activation is looked up once per tenant per tick, not per record.
- **A unified dead-letter queue.** `integrations.dead-letter` lists give-up rows from both directions; `integrations.requeue` resets one to retryable once its root cause is fixed (verified end to end: a broken API key drove event → retry → retry → dead-letter, then fix-secret + requeue → success).
- **Retention.** A janitor trims dispatched outbox rows, processed inbox/task rows, old run history and expired idempotency records past a window (default 30 days) so the hot loops don't scan unbounded tables. Audit and dead-lettered rows are never auto-trimmed — those are records, not scratch.
- **Manifest is conditional.** The manifest carries an `ETag` over its content revision + the actor's permissions and answers `304` to `If-None-Match`, skipping the reflection rebuild + serialization on every page load.

## Ceilings (v1, honest)

- **At-least-once, handler-idempotent.** Event and schedule runs can repeat (a redelivery, a lapsed lease); handlers must tolerate it, as the docs/10 inbound side already requires. A per-run idempotency token is a natural extension.
- **Backoff is deterministic (no jitter).** Fine at this scale; jitter to avoid a thundering herd at 100× tenants sharing a spec is a later addition.
- **SSE cross-instance (done).** Live refresh now fans out through `IEffectBackplane`: in-process for single-node/SQLite, and a Postgres `LISTEN/NOTIFY` adapter (`AddTamPostgresBackplane`) for multi-node — a grid open on instance B refreshes from a commit on instance A, no new infrastructure. Verified: a second `LISTEN`er received the app's `NOTIFY` on commit. Payloads over ~8 KB fall back to local-only delivery (NOTIFY's cap); a Redis backplane is the same seam if ever needed.
- **Secrets rotation** relies on the Data-Protection key ring; a rotated-away key makes a secret undecryptable (treated as "not configured") rather than silently wrong — re-set the secret. The ring is DB-persisted so it survives restarts and is shared across instances.
- **No secret versioning / audit of secret *access*** — only of integration runs.

## Phasing

- **S1 (implemented)**: vault (Data Protection, DB-persisted key ring, per-tenant purpose) + settings/secrets operations, outbound integrations with event/schedule/manual triggers, SSRF-guarded egress, scheduler with a claim-first multi-node lease + per-run timeout, run history, reserved-permission gate, the Fortnox two-way demo.
- **S2 (implemented)**: outbound retry/backoff unified with the inbox (shared `RetryPolicy`), outbound task queue + retry driver, outbox claim-lease + poison dead-letter, unified `integrations.dead-letter` + `integrations.requeue`, retention janitor, manifest ETag/304.
- **S3**: per-run idempotency tokens; cross-instance SSE backplane; cron-grade schedule specs; secret-access audit; a secrets admin UI page (today: API/MCP, plus settings/secrets grids are trivial to bind).
