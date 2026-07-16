# Step 12 — Six months later: change impact *(the unified report: DESIGNED, NOT BUILT)*

A developer adds `CustomerReference` to `CreateOrder.Input` as a required field. The design target is a single build-time answer:

```
Added CreateOrder.Input.CustomerReference (required)

✓ HTTP + OpenAPI schema updated
✓ Web create form: field added
✓ MCP tool schema updated
✓ TypeScript client regenerated
✗ INT001 fortnox.orders.import does not map required field CustomerReference
✗ MANIFEST: non-additive change vs. release baseline — requires baseline approval (D4)
```

**That consolidated report is not generated yet.** What exists today are its two red lines, separately: the D4 baseline check (`scripts/check_manifest.py`) fails CI on any non-additive manifest change — a new required input included — until the new baseline is consciously committed; and a *typed* integration that stops mapping a required field fails model build with `INT001` (the wire-mapped fortnox plugin instead fails per row, Step 10). The manifest's `gatedBy`/`subscribedBy` sections (Steps 13 and 17) carry the plugin-coupling half of the story. The green checkmarks above are true but silent — the derivation chain simply regenerates. What's missing is the tool that says all of this in one place.

---
