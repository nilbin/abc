# Step 12 — Six months later: change impact *(BUILT)*

A developer makes the order type mandatory on order creation, adds an amount column to the
time list, and enriches the order-created event. Before anything ships, one command answers
"what does this touch?" — no server, no database, safe anywhere the code compiles:

```bash
dotnet run --project samples/erp -- impact samples/erp/manifest.baseline.json
```

```
impact vs samples/erp/manifest.baseline.json:
CHANGED operation orders.create
    field orderType ADDED (required)
    ✓ HTTP endpoint + OpenAPI + MCP tool schema update from the model
    ✓ form(s) re-render from the manifest: web.orders.create
    ✓ TypeScript client: regenerate (scripts/generate-types.mjs) — CI baseline gate reminds
    ! integration 'fortnox.orders.import' maps into this operation — verify its mapping
CHANGED view time.list
    field amount ADDED (required)
    ✓ HTTP endpoint + OpenAPI + MCP tool schema update from the model
    ✓ grid(s) re-render from the manifest: web.time.list
    ✓ TypeScript client: regenerate (scripts/generate-types.mjs) — CI baseline gate reminds
    ! plugin 'invoicing' reads this view (declared contract: id, workOrderNumber, amount, status)
CHANGED event order-created +[orderType]
    ! subscriber 'inspect' consumes this payload — verify its reads

✗ NON-ADDITIVE vs release baseline (D4) — CI fails until the new baseline is consciously
  re-exported and committed:
    ✗ operation orders.create: new REQUIRED field 'orderType' breaks existing callers
```

(Real output — this exact report is what the sample host prints when its baseline is rolled
back to before those three changes.)

Three severities, and the philosophy is that **additive-and-invisible is not a thing**:

- **✓ the silent greens, stated.** Schemas, forms, grids, MCP tools and the TS client all
  regenerate from the model — the report *claims* it per change instead of leaving "nothing
  to do" as an absence the developer has to trust.
- **✗ the D4 red line.** The breaking classification is the same one
  `scripts/check_manifest.py` enforces in CI (removed anything, type change, optional →
  required, new required input, permission change) — the report SHOWS what the gate will
  fail, before the push. The process stays: an intentional break is approved by re-exporting
  and committing the baseline in the same PR.
- **! the couplings.** Everything the manifest knows about who depends on the changed
  surface: plugins gating the operation (`gatedBy`), subscribers of the event
  (`subscribedBy`), integrations mapping into the operation, and plugin view-contracts
  (`RequiresView`) over the changed view — including the hard case where a required
  contract field is no longer served, which is both listed per plugin and counted as a
  break.

Mechanics: `TamImpact.Against(model, baseline)` (Tam.Core) diffs the compiled model's
manifest against the committed baseline `ManifestDto`; the host CLI mode
(`dotnet run -- impact [path]`, wired by the same `TamManifestExport.TryHandle` call from
Step 0) prints it and exits 2 on breaks so scripts can branch. CI's authoritative gate
remains the baseline check — the impact report is the developer-facing half of the same
contract.
