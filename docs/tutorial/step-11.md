# Step 11 — Tests exercise the contract, not the plumbing *(BUILT)*

`Tam.Testing` runs the REAL pipeline in-process — authorization, structural validation,
gates, transaction, three-way merge, audit, outbox — against a real database provider, with
no HTTP in the loop. What is green in a feature's test file is what is true on the wire,
because it is the same pipeline. The samples below are lifted verbatim from
`samples/erp.Tests` (run them: `dotnet test samples/erp.Tests`).

The model is a VALUE, so the composition root builds it once and both hosts consume it —
`Program.cs` serves it, the test host verifies it:

```csharp
// samples/erp/ErpModel.cs
public static class ErpModel
{
    public static TamModel Build() => new TamModelBuilder()
        .DefaultCulture("sv")
        // ... the entire model: operations, views, forms, grids, pages, nav, plugins ...
        .Build();
}

// samples/erp.Tests — an isolated in-memory SQLite database per test class; swap
// CreateSqliteAsync for CreateAsync(model, o => o.UseNpgsql(...)) to be provider-true in CI.
host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
```

Actors are minted as (tenant, grant-set) pairs — paired atoms, `"*"`, and reserved
permissions behave exactly as in production:

```csharp
var actor = host.Actor("demo", "orders.create");
var response = await actor.ExecuteAsync("orders.create", new
{
    customerId,
    orderType = "service",
    workAddress = "Testgatan 1",
    description = "Byt packning",
});
response.ShouldSucceed();
```

The assertions speak the envelope's language — findings by code, conflicts by field,
effects by contract:

```csharp
response.ShouldFailWith("orders.invalid-customer");        // domain finding, stable code
response.ShouldBeDenied();                                 // pipeline.not-authorized
stale.ShouldConflictOn("name");                            // three-way merge conflict (docs/07)
completed.ShouldSucceed().ShouldPublish("work-order-completed");   // event contract (docs/31)
var id = created.Output<CreateProject.Output>().ProjectId; // typed output
```

Ownership is testable the same way — two actors, no authorization code anywhere:

```csharp
var tekla  = host.ActorWithId("demo", teklaId,  "time.book", "time.read");
var didrik = host.ActorWithId("demo", didrikId, "time.book", "time.read", "time.read-all");
// each books an entry ...
Assert.Equal(1, (await tekla.QueryAsync("time.list")).ShouldSucceed().Total);   // own scope
Assert.Equal(2, (await didrik.QueryAsync("time.list")).ShouldSucceed().Total);  // -all widens
```

## The capability sweep: the whole setup, verified in one test

Declared capabilities are contract *and* implementation (Step 5) — so the harness can
verify ALL of them mechanically. `CapabilitySweep` executes every view's default sort,
every sortable field in both directions, and every filterable field with a type-appropriate
probe (`=`, `.contains`, `.from`, `.to`), through the pipeline, against the real provider:

```csharp
[Fact]
public async Task Every_declared_capability_executes()
{
    await using var host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
    (await CapabilitySweep.RunAsync(host, "demo")).ThrowIfFailed();
}
```

A view that compiles but cannot TRANSLATE — the bug class TAM007 exists for: green build,
500 on the first sorted request — fails here, named (`view ?sort=... → error`), before it
ships. Zero rows suffice: SQL generation is the thing under test. And because the sweep
derives everything from the model, every aggregate anyone adds later — host or plugin — is
covered the moment it is declared, with no test written.

Failure text is runner-agnostic (`TamAssertionException`), so xUnit, NUnit and MSTest all
render it intact.

What this harness deliberately does NOT cover: HTTP concerns (auth handshakes, headers,
status codes — the wire suites' job), the React runtime, and cross-instance behavior
(SSE backplane, outbox dispatch timing). Those stay verified against the running sample.

**The outbox is under the TEST'S control:** the harness runs no background loops, so an
operation's `ShouldPublish` asserts the outbox ROW — subscribers have not fired yet. When a
test needs subscriber effects (a plugin reacting to an event), it says so:

```csharp
(await admin.ExecuteAsync("orders.create", new { ... })).ShouldSucceed();
await host.DispatchOutboxAsync();   // the production dispatch pass, on demand
// ...assert what the subscriber wrote
```

`DispatchOutboxAsync` drains every due row with production semantics (claim-lease, tenant
pinning per record, plugin-activation gating, poison isolation) and returns how many rows
finished. Deterministic by construction — no polling, no timing (an M6 RTFM finding: the
first consumer had to hand-build this; now it is the API).

## The harness surface (the whole of it)

- `TamTestHost<TDb>.CreateSqliteAsync(model)` — isolated in-memory SQLite, schema created,
  gone on dispose. `CreateAsync(model, options => options.UseNpgsql(...), configureServices)`
  is the provider-true overload.
- `host.Actor(tenant, ...atoms)` — a (tenant, grant-set) caller. `ActorWithId(tenant, id,
  ...atoms)` fixes the actor id for own-scope scenarios (the id rows record as owner).
- `actor.ExecuteAsync(operationId, input, idempotencyKey?)` — full pipeline; `input` is any
  object whose JSON matches the Input record (`Change` fields:
  `new { original = ..., value = ... }`).
- `actor.QueryAsync(viewId, query?)` — wire-shaped query params (`sort`, `page`, filters,
  `ext.key`).
- `host.SeedAsync(tenant, db => ...)` — arrange state in an ambient tenant scope (global
  filter + stamping active); saves on return. `QueryDbAsync(tenant, db => ...)` is the
  read twin.
- `host.DispatchOutboxAsync()` — above. `host.Model` — the built model.
- `CapabilitySweep.RunAsync(host, actor factory)` — next section.
- There is deliberately NO service-provider accessor: the pipeline is the API. Needing one
  is a signal the scenario belongs in a wire suite.

**Testing plugin activation**: entitle the plan, then activate through the front door —
seed a `SubscriptionEntity` (`EntitlementsJson: ["yourplugin"]`) in `SeedAsync`, then
`ExecuteAsync("plugins.activate", new { pluginId = "yourplugin" })` as an actor holding
`plugins.manage`. That is the admin's real path, entitlement gate included.

---
