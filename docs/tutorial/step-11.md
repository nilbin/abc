# Step 11 — Tests exercise the contract, not the plumbing *(the harness: DESIGNED, NOT BUILT)*

The design calls for an `OperationTest` base that runs the real pipeline — authorization, transaction, merge, audit — against a real database, with `Given.*` fixtures and assertions like `result.ShouldFailWith("orders.invalid-customer")` or `ShouldHaveEffect<EntityCreated>(e => e.Entity == "order")`, so that what's green in a feature's test file is what's true in production. **That harness does not exist yet.** Nothing ships it, and no sample uses it — treat any `OperationTest` snippet you find in older drafts as fiction.

What you write today is ordinary xUnit against the framework's real seams — this one is `tests/Tam.Tests/MergeTests.cs`, verbatim:

```csharp
[Fact]
public void Non_overlapping_stale_edit_merges_cleanly()
{
    var doc = new Doc();
    // Current Description already moved to "Replace pump" by user B:
    TamMerge.Apply(doc, new EditInput(Description: new("Repair pump", "Replace pump")));

    // User A, holding the old base, edits a DIFFERENT field:
    var merge = TamMerge.Apply(doc, new EditInput(
        RequestedDate: new(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 22))));

    Assert.False(merge.HasConflicts);
    Assert.Equal(["requestedDate"], merge.AppliedFields);
}
```

The framework's own suite covers the pipeline at this level — merge semantics, paired-atom scoping, tenant isolation, plugin seams, RLS — and the Step 16/17 scenarios run as wire suites against the running samples. Manifest pinning exists as the D4 baseline check (Step 0), not as per-feature snapshot tests.

---
