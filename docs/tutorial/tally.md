# The tally

**Written by hand** (the only independently maintained facts):

| File | Approx. lines | Decisions it owns |
| --- | --- | --- |
| `Domain/Orders.cs` + shared `ValueTypes.cs` (the orders slice) | ~80 | invariants, value types, status transitions |
| `Features/Orders.cs` | ~290 | operations, shared rules, derivations, views — every business decision |
| `Program.cs` (composition root: model half) | ~110 | forms, grids, nav, pages, slots, event contracts |
| `Db.cs` | ~60 | EF mapping, plugin storage opt-ins, the tenant boundary |
| `samples/fortnox` (the plugin) | ~120 | external mapping, idempotency, outbound pushes |
| `locales/sv.json` + `locales/en.json` | ~100 | every word a human reads, per culture |

**Derived, and therefore never drifting:** four HTTP endpoints and their OpenAPI, JSON Schemas, a typed TypeScript client, create/edit forms with reactive behavior, a grid with paging/sorting/filtering/search and gated actions, three-way merge and structured conflicts, audit, idempotency, outbox events, the MCP tools plus a resolve surface, integration inbox/retry/replay, and per-tenant custom-field participation across every one of those boundaries.

That ratio — roughly 750 handwritten lines owning every real decision, and everything mechanical derived — is the success criterion of [18-success-criteria.md](../18-success-criteria.md) made concrete. The implementation runs this document top to bottom today: Steps 0–10 and 13–18 are the running samples; where a step is still design (the test harness of Step 11, the unified report of Step 12), it says so on the step.
