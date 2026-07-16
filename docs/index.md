# Tam — the Typed Application Model

Tam is a model-driven framework for multi-tenant business applications on .NET and React: you
declare **entities, operations, views, and bindings** in C#; the compiled model produces the
API, the manifest, the generic UI, localization, audit, tenant isolation, and an MCP surface
for agents — and a **plugin system** lets vendors extend a running product per tenant without
the host ever naming them.

The reference host is a small Swedish ERP (Norrservice). Everything documented as BUILT is
verified end to end against it — unit suite, wire suites over HTTP on SQLite **and**
PostgreSQL, and rendered-UI checks.

## Start here

- **[The tutorial](tutorial/index.md)** — one feature end to end, then custom fields, plugins,
  integrations, tenancy, and the composed UI. One page per step; every BUILT step's code is
  verbatim from the wire-verified sample.
- **[Progress](status.md)** — the honest ledger: what runs, what's verified, what's still
  design. Updated with every milestone.
- **[Decisions](19-decisions.md)** — the architectural decision ledger (D1–D9).
- **[Code structure](29-code-structure.md)** — the repo map and where things go.

## For LLMs

Machine-readable index at [`llms.txt`](llms.txt) — a one-file map of every page with
one-line descriptions, following the llms.txt convention.
