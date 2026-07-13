# 16 — Package Structure and Repository Layout

> Package names below use the `ProductModel.*` placeholder from the original plan. Final naming is an open question ([review-notes.md](review-notes.md)).

## Suggested NuGet packages

```
ProductModel.Core
ProductModel.Compiler
ProductModel.AspNetCore
ProductModel.EntityFrameworkCore
ProductModel.Forms
ProductModel.TypeScript
ProductModel.Mcp
ProductModel.Integrations
ProductModel.Extensions        ← tenant field registry + extension runtime (see 15)
ProductModel.Testing
```

### ProductModel.Core

Contains only framework-neutral abstractions:

```
Operation<TInput,TOutput>
View<TQuery,TResult>
Change<T>
Finding
FieldPath
DerivationResult
OperationRequest<T>
OperationResult<T>
OperationEffect
ExtensionData / ExtensionFieldDefinition (contracts only)
```

No ASP.NET Core, EF Core, React, or MCP dependencies.

### ProductModel.Compiler

- Incremental source generator
- Analyzers
- Manifest generation
- Dependency graph
- Impact analysis
- Diagnostics

### ProductModel.AspNetCore

- Endpoint mapping
- OpenAPI
- Authorization integration
- Error responses
- Idempotency
- Execution pipeline
- Correlation

### ProductModel.EntityFrameworkCore

- Transaction integration
- EF model inspection
- Outbox
- Change-set application
- Three-way merging
- Effect extraction
- Persistence diagnostics

### ProductModel.Forms

- Binding model
- Derivation execution
- Field state resolution
- Batched server evaluation
- Form revision handling
- Reset policies
- Findings
- Conflict metadata

### ProductModel.TypeScript

- Type generation
- Client generation
- Manifest generation
- Frontend contracts

### ProductModel.Mcp

- Operation-to-tool adapter
- View-to-resource adapter
- Derivation preflight tools
- Elicitation support

### ProductModel.Integrations

- Mapping compiler
- Inbox and outbox abstractions
- Idempotency
- Replay
- Checkpoints
- Reconciliation contracts

### ProductModel.Extensions

- Tenant field registry (definitions, lifecycle, revisioning)
- Registry-time diagnostics (`EXT###`) sharing rule implementations with the compiler
- Effective-manifest overlay merging and caching
- Extension change-set validation and application
- JSONB storage integration and index promotion

### ProductModel.Testing

- Operation test host
- Derivation test helpers
- Binding snapshot tests
- Conflict test helpers
- Integration mapping tests
- Manifest assertions
- Extension field fixtures (registry-in-memory, overlay assertions)

## Repository structure for an application

```
src/
  Product.Domain/
    Orders/
    Customers/
    Invoicing/
  Product.Application/
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
    Customers/
    Invoicing/
  Product.Integrations/
    Fortnox/
    BusinessCentral/
    Legacy/
  Product.Host/
    Program.cs
apps/
  web/
    src/
      renderers/
      overrides/
      generated/
  admin/
    src/
      renderers/
      overrides/
      generated/
  mobile/
    src/
      renderers/
      overrides/
      generated/
tests/
  journeys/
    web/
    admin/
    mobile/
```

Use one modular monolith:

- One API host
- One PostgreSQL database
- One frontend build per application
- Optional worker mode

Possible executable modes:

```
product api
product worker
product migrate
```
