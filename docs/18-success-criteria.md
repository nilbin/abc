# 18 — Success Criteria

The framework succeeds when a normal business feature requires developers to write only:

- Domain behavior
- Operation input/output
- Operation execution
- Read query
- Exceptional derivations
- Exceptional presentation
- External mapping
- Tests

Developers should **not** manually maintain:

- Controllers
- Duplicate DTOs
- Separate validators for structural rules
- Frontend API clients
- Basic form schemas
- Basic grid schemas
- MCP tool wrappers
- Audit wrappers
- Outbox wrappers
- Integration retry loops
- Whole-entity edit payloads
- Ad hoc conflict handling

## Representative feature

```
Orders/
  Create/
    Operation.cs
    Derivations.cs
    Bindings.cs
    Tests.cs
  EditDetails/
    Operation.cs
    Bindings.cs
    Tests.cs
  Complete/
    Operation.cs
    Tests.cs
  List/
    View.cs
    Bindings.cs
    Tests.cs
```

## Extensibility success criteria

Tenant customization succeeds when:

- A tenant admin adds a typed custom field and it appears in forms, grids, reports, exports, audit, and MCP schemas **without any deployment** and without any developer involvement.
- A developer makes an aggregate extensible by adding **one interface and one opt-in per binding** — nothing more.
- No extension field can affect a compiled business decision, and the analyzer proves it.
- Promoting a proven custom field to compiled code is a scaffolded, mechanical task with a data migration — not a rewrite.

## The final architectural principle

> Write typed business behavior and read models once. Use derivations to resolve reactive interaction state. Bind operations and views to each boundary. Persist only explicit changes. Derive every mechanical representation and reject contradictions at compile time.
