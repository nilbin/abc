# 16 — Package Structure and Repository Layout

> Naming decided: `Tam.*` (**T**yped **A**pplication **M**odel) for NuGet packages and `@tam/*` for frontend packages, replacing the original `ProductModel.*` placeholder — "product" collided with the domain word. The `Product.*` names in the application layout below are the *example application's* namespace, not the framework's.

## Suggested NuGet packages

```
Tam.Core
Tam.Compiler
Tam.AspNetCore
Tam.EntityFrameworkCore
Tam.Forms
Tam.TypeScript
Tam.Mcp
Tam.Integrations
Tam.Extensions        ← tenant field registry + extension runtime (see 15)
Tam.Testing
```

### Tam.Core

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

### Tam.Compiler

- Incremental source generator
- Analyzers
- Manifest generation
- Dependency graph
- Impact analysis
- Diagnostics

### Tam.AspNetCore

- Endpoint mapping
- OpenAPI
- Authorization integration
- Error responses
- Idempotency
- Execution pipeline
- Correlation

### Tam.EntityFrameworkCore

- Transaction integration
- EF model inspection
- Outbox
- Change-set application
- Three-way merging
- Effect extraction
- Persistence diagnostics

### Tam.Forms

- Binding model
- Derivation execution
- Field state resolution
- Batched server evaluation
- Form revision handling
- Reset policies
- Findings
- Conflict metadata

### Tam.TypeScript

- Type generation
- Client generation
- Manifest generation
- Frontend contracts

### Tam.Mcp

- Operation-to-tool adapter
- View-to-resource adapter
- Derivation preflight tools
- Elicitation support

### Tam.Integrations

- Mapping compiler
- Inbox and outbox abstractions
- Idempotency
- Replay
- Checkpoints
- Reconciliation contracts

### Tam.Extensions

- Tenant field registry (definitions, lifecycle, revisioning)
- Registry-time diagnostics (`EXT###`) sharing rule implementations with the compiler
- Effective-manifest overlay merging and caching
- Extension change-set validation and application
- JSONB storage integration and index promotion

### Tam.Testing

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
