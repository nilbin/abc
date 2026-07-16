# Tutorial: A Complete Feature, End to End

This walkthrough builds a complete **Work Orders** feature for a fictional field-service company. It shows every file a developer writes and, after each step, what the framework derives from it. It began life as the executable specification of the developer experience; the framework now exists, and the code samples in Steps 0–10 are lifted from the running sample (`samples/erp` and its sibling plugins) — if a sample here drifts from the source, the document is wrong. Where something is still design rather than code, the step says so: **BUILT** means verified against the source; *(designed, not built)* means the vision is stated but nothing enforces it yet.

**The scenario.** Customers call in service jobs. An order is created for a customer, optionally linked to a project, carries a work address and description, and is eventually completed. Back-office staff use the web app, technicians use mobile, the Fortnox integration imports orders, and agents create orders over MCP. One tenant wants a "Machine serial number" field on orders.

**What we will write:** one domain file, one feature file, a composition root, one integration plugin. **What we will not write:** controllers, DTOs, validators, API clients, form schemas, grid schemas, MCP wrappers, audit code, or conflict handling.

```
samples/erp/
  Erp.csproj             the build contract                    (Step 0)
  Db.cs                  the DbContext                         (Step 0)
  Program.cs             the composition root: model + host    (Steps 0, 4, 5, 18)
  Domain.cs              entities, value types, findings       (Step 1)
  Features/Orders.cs     operations, derivations, views        (Steps 2–7)
  Features/Customers.cs  the same pattern, smaller
  locales/               sv.json  en.json                      (Step 1)
samples/fortnox/         the Fortnox integration, as a plugin  (Step 10)
```

Note what is absent: no per-feature `Bindings.cs`. Bindings — forms, grids, nav, pages — are declared in the composition root, one screen apart from each other and from the pipeline that serves them (docs/29).

---

## The steps

- [Step 0 — A new host from nothing](step-00.md) *(BUILT — `samples/erp`)*
- [Step 1 — Domain state](step-01.md) *(BUILT)*
- [Step 2 — The create operation](step-02.md) *(BUILT)*
- [Step 3 — Derivations: reactive form behavior, written once](step-03.md) *(BUILT)*
- [Step 4 — Bindings: one per boundary that differs](step-04.md) *(BUILT)*
- [Step 5 — The list: a view and its grid](step-05.md) *(BUILT)*
- [Step 6 — Editing: partial, conflict-safe](step-06.md) *(BUILT)*
- [Step 7 — Completing: an intent, not an edit](step-07.md) *(BUILT)*
- [Step 8 — What the machine callers see](step-08.md) *(BUILT)*
- [Step 9 — The tenant adds a custom field. Nobody deploys anything.](step-09.md) *(BUILT)*
- [Step 10 — The integration is a mapping, not a sync engine](step-10.md) *(BUILT — `samples/fortnox`)*
- [Step 11 — Tests exercise the contract, not the plumbing](step-11.md) *(the harness: DESIGNED, NOT BUILT)*
- [Step 12 — Six months later: change impact](step-12.md) *(the unified report: DESIGNED, NOT BUILT)*
- [Step 13 — A partner ships a plugin](step-13.md) *(implemented — [22-plugins.md](../22-plugins.md), decision D8; running in samples/inspect)*
- [Step 14 — Who is asking, and what have they paid for](step-14.md) *(implemented — [24-subscriptions.md](../24-subscriptions.md))*
- [Step 15 — Norrservice becomes a group](step-15.md) *(implemented — [26](../26-tenancy-hierarchy-and-identity.md) + [27](../27-authorization-model.md))*
- [Step 16 — Approvals arrive as a plugin — and the domains never notice](step-16.md) *(BUILT — the seams and the package, `samples/approvals`)*
- [Step 17 — Invoicing arrives as a plugin — and Orders still doesn't know](step-17.md) *(BUILT — [31-cross-domain-plugins.md](../31-cross-domain-plugins.md), `samples/invoicing`)*
- [Step 18 — The composed UI: nav, pages, slots, and the subtree grid](step-18.md) *(BUILT — [30-navigation.md](../30-navigation.md), [32-pages.md](../32-pages.md), docs/26 D-H1)*
- [The tally](tally.md)
