# 05 — Derivations

Status: design-era doc — the CONCEPTS hold, but not every API sketch below exists. **What is
BUILT** (an M3 RTFM finding — this box is the source of truth until the doc is rewritten):

- Client-side: the portable AST rules `VisibleWhen` / `RequiredWhen` / `OnSourceChange`
  (docs/06 bindings) — evaluated from the manifest with no server round-trip.
- Server-side: `[ServerDerivation("id")]` + `[DependsOn(...)]` methods returning
  `DerivationResult`, whose REAL surface is `Suggest(member, value)`,
  `AddOptions(member, options)`, `AddWarning(factory)`, `AddFieldError(member, factory)`,
  `Add(finding)`, `From(result, target)`, `Merge(other)` — served by
  `POST /api/forms/{formId}/resolve` with body `{ "input": {...}, "changed": ["field"],
  "revision": n }` (a flat body answers 400 naming the shape).
- NOT built (kept below as design): `DerivedValue` (forced computed fields),
  `field.DefaultOnceFrom` / `SuggestFrom` / `DerivedFrom` fluent bindings, the
  `Preserve`/`Clear`/`ClearIfInvalid` policies (`RecomputeIfUntouched` is the one shipped
  `DependentValuePolicy`), and dependency-cycle detection (FORM001). A computed
  display-only value today rides a `Suggest` onto an input field the server ignores — the
  docs/34 friction log tracks the wart.

A derivation computes **interaction state** without changing durable business state.

Derivations may:

- Change field visibility
- Change enabled or read-only state
- Change requiredness
- Load allowed selections
- Produce defaults
- Produce suggestions
- Produce calculated transient values
- Produce warnings
- Produce field errors
- Perform server-side validation
- Query databases
- Query external services

Derivations may **not**:

- Persist business state
- Reserve stock
- Send external messages
- Create external records
- Perform irreversible actions

The boundary is:

> Derivations explain and shape what may happen. Operations make it happen.

## Example

```csharp
[Derivation("orders.create.customer-state")]
[DependsOn<CreateOrder.Input>(
    nameof(CreateOrder.Input.CustomerId),
    nameof(CreateOrder.Input.EstimatedTotal))]
public static async Task<DerivationResult> DeriveCustomerState(
    CreateOrder.Input input,
    DerivationContext context,
    CancellationToken cancellationToken)
{
    if (input.CustomerId is null)
        return DerivationResult.Empty;

    var customer = await context.Db.Customers
        .Where(x => x.Id == input.CustomerId)
        .Select(x => new
        {
            x.IsActive,
            x.VisitAddress,
            x.CreditBlocked
        })
        .SingleOrDefaultAsync(cancellationToken);

    if (customer is null)
    {
        return DerivationResult.FieldError(
            nameof(input.CustomerId),
            CustomerFindings.NotFound);
    }

    var result = DerivationResult.Empty;

    if (!customer.IsActive)
    {
        result = result.AddFieldError(
            nameof(input.CustomerId),
            CustomerFindings.Inactive);
    }

    if (customer.CreditBlocked)
    {
        result = result.AddWarning(CustomerFindings.CreditBlocked);
    }

    return result.Suggest(
        nameof(input.WorkAddress),
        customer.VisitAddress);
}
```

## Derivation output

Derivations should resolve complete field state:

```csharp
public sealed record ResolvedFieldState(
    bool Visible,
    bool Enabled,
    bool Required,
    object? SuggestedValue,
    object? DerivedValue,
    IReadOnlyList<Option>? Options,
    IReadOnlyList<Finding> Findings);
```

A finding should be structured:

```csharp
public sealed record Finding(
    string Code,
    FindingSeverity Severity,
    IReadOnlyDictionary<string, object?> Args,
    IReadOnlyList<FieldPath> Targets,
    bool BlocksSubmission,
    string? Message);   // resolved at the boundary in the request culture — never authored in code
```

Findings are created through **finding factories** (`CustomerFindings.Inactive = Finding.Error("customers.inactive")`) that carry a stable code plus structured args; message text lives in per-culture resources and is resolved at the boundary ([21-localization.md](21-localization.md)).

Supported severities:

- `Information`
- `Warning`
- `Error`

## Pure and contextual derivations

Support two categories.

### Portable derivations

Pure deterministic logic that can run in frontend and backend:

```csharp
[PortableDerivation]
public static bool ProjectRequired(CreateOrder.Input input)
    => input.OrderType == OrderType.Project;
```

### Contextual derivations

Require database state, permissions, current time, or external systems:

```csharp
[ServerDerivation]
public static Task<DerivationResult> AvailableProjects(
    CreateOrder.Input input,
    DerivationContext context,
    CancellationToken cancellationToken);
```

Do not attempt to translate arbitrary C# into TypeScript. Only a **deliberately constrained portable expression model** should be cross-compiled.

> The portable expression model is a first-class artifact of the framework, not a code-generation detail: the same expression AST is produced by the source generator when lowering `[PortableDerivation]` C#, and authored directly as data by tenant-defined visibility/requiredness rules ([15-extensibility.md](15-extensibility.md)). One AST, one client evaluator, one server evaluator.

## Reactive dependency graph

The compiler should understand dependencies:

```
ProjectId visibility     depends on OrderType
ProjectId requiredness   depends on OrderType
ProjectId options        depend on CustomerId and OrderType
WorkAddress suggestion   depends on CustomerId
```

The runtime should support:

- Dependency invalidation
- Batched server evaluation
- Debouncing
- Blur-based validation
- Submit-time evaluation
- Request cancellation
- Monotonic form revisions
- Stale response rejection
- Cycle detection

## Value update policies

Explicitly distinguish:

- **Default once**
- **Suggestion**
- **Forced derived value**

Examples:

```csharp
field.DefaultOnceFrom(CustomerSummary.VisitAddress);

field.SuggestFrom(CustomerSummary.VisitAddress);

field.DerivedFrom(CustomerSummary.VisitAddress)
    .When(x => x.UseCustomerAddress);
```

Dependency changes need explicit policies:

- `Preserve`
- `Clear`
- `ClearIfInvalid`
- `RecomputeIfUntouched`
- `RequireConfirmation`

Default: `RecomputeIfUntouched` for suggested or defaulted values.
