# 01 — Overview and Principles

## Goal

Build a minimalistic .NET application framework that removes repetitive backend, frontend, integration, and agent-facing code without becoming a low-code platform or replacing normal programming.

The system should let developers define business behavior once and derive all predictable boundary representations from it.

The core principle is:

> Write domain state, operations, read models, derivations, and exceptional boundary choices once. Generate or infer everything mechanical downstream.

The framework optimizes for:

- Minimal independently maintained facts
- Minimal DTO and validation duplication
- Multiple frontend applications
- Backend-defined forms and grids without backend-defined pixels
- Reactive and server-validated forms
- Conflict-safe partial edits
- External integrations
- MCP and agent usage
- Compile-time verification
- Easy code navigation for humans and coding agents
- One modular monolith rather than microservice complexity

## Core architecture

The application consists of five concepts:

1. **Domain State** — [02-domain-state.md](02-domain-state.md)
2. **Operations** — [03-operations.md](03-operations.md)
3. **Views** — [04-views.md](04-views.md)
4. **Derivations** — [05-derivations.md](05-derivations.md)
5. **Bindings** — [06-bindings.md](06-bindings.md)

Tenant extensibility ([15-extensibility.md](15-extensibility.md)) is deliberately *not* a sixth concept: runtime-defined custom fields flow through the same five concepts as a second authoring channel.

## Development principles

### Author independent decisions once

Examples of independent decisions:

- Storage length
- Business invariant
- Operation input
- Default label
- Frontend visibility
- Integration ownership
- Conflict policy

Do not collapse genuinely separate decisions merely because they concern the same value. The goal is not absolute DRY. The goal is:

> Every independent decision is authored once, and every dependent representation is derived.

### Prefer inference

Infer from:

- C# type
- Nullability
- Value type
- Operation input
- View output
- EF mapping
- Naming conventions

Only add metadata when the compiler cannot determine the correct answer. The framework should become quieter over time.

### Keep runtime magic minimal

Prefer:

- Compile-time manifests
- Compile-time diagnostics
- Explicit operation registration
- Inspectable dependency plans
- Normal C# handlers
- Normal LINQ
- Normal EF Core

Avoid:

- Runtime assembly scanning with hidden behavior
- Arbitrary expression evaluation
- Reflection-heavy pipelines
- Generic entity mutation
- Dynamic component trees
- Hidden cross-entity saves

### Escape into normal code

All abstractions must have normal-code escape hatches:

- Custom handler
- Custom LINQ query
- Custom renderer
- Custom integration mapping
- Custom validation rule
- Custom operation

Using an escape hatch should not bypass security, authorization, transaction handling, or operation contracts. The compiler should report which guarantees are reduced.

## Explicit non-goals

The project is **not**:

- An ORM
- A generic repository
- A frontend component framework
- A workflow engine
- An event-sourcing framework
- A microservice framework
- A visual application builder
- A generic CRUD engine
- An iPaaS
- A low-code platform
- A replacement for ASP.NET Core
- A replacement for EF Core
- A replacement for normal domain code
- A JSON programming language
- A real-time collaborative editor (presence, live co-editing, field locking — see decision D5 in [19-decisions.md](19-decisions.md))

Its responsibility is:

> Derive and verify boundary representations around typed operations, views, derivations, and bindings.

## The final architectural principle

> Write typed business behavior and read models once. Use derivations to resolve reactive interaction state. Bind operations and views to each boundary. Persist only explicit changes. Derive every mechanical representation and reject contradictions at compile time.
