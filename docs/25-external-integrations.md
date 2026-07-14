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

## Ceilings (v1, honest)

- **At-least-once, handler-idempotent.** Event and schedule runs can repeat (a scheduler restart, an outbox redelivery); handlers must tolerate it, as the docs/10 inbound side already requires. A per-run idempotency token is a natural extension.
- **No retry/backoff on outbound failures yet** — a failed run is recorded but not automatically retried; the schedule's next tick or a manual run re-drives it. (The inbound inbox has retry + dead-letter; unifying the two is future work.)
- **Scheduler is single-node.** Two app instances would both tick; a `SELECT … FOR UPDATE SKIP LOCKED` lease on the schedule row is the multi-node story.
- **Secrets rotation** relies on the Data-Protection key ring; a rotated-away key makes a secret undecryptable (treated as "not configured") rather than silently wrong — re-set the secret. Production persists and backs up the key ring.
- **No secret versioning / audit of secret *access*** — only of integration runs. A "who read which secret when" trail is a later addition.

## Phasing

- **S1 (implemented)**: vault (Data Protection) + settings/secrets operations, outbound integrations with event/schedule/manual triggers, scheduler, run history, the Fortnox two-way demo.
- **S2**: outbound retry/backoff unified with the inbox; per-run idempotency tokens; multi-node scheduler lease.
- **S3**: cron-grade schedule specs; secret-access audit; a secrets admin UI page (today: API/MCP, plus settings/secrets grids are trivial to bind).
