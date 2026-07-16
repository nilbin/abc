# Step 3 — Derivations: reactive form behavior, written once *(BUILT)*

Three facts about order creation are interactive: project fields only matter for project orders; project options depend on the chosen customer; picking a customer should validate them and suggest an address.

```csharp
// samples/erp/Features/Orders.cs

public static class CreateOrderDerivations
{
    // Portable rules live on the FORM BINDING as expression lambdas (Step 4): lowered to the
    // wire AST, evaluated client-side instantly —
    //   form.Field(x => x.ProjectId)
    //       .VisibleWhen(x => x.OrderType == OrderType.Project)
    //       .RequiredWhen(x => x.OrderType == OrderType.Project);

    // Contextual: needs the database, runs server-side, batched + debounced
    [ServerDerivation("orders.create.available-projects")]
    [DependsOn(nameof(CreateOrder.Input.CustomerId), nameof(CreateOrder.Input.OrderType))]
    public static async Task<DerivationResult> AvailableProjects(
        CreateOrder.Input input, DerivationContext context, ErpDbContext db, CancellationToken ct)
    {
        if (input.OrderType != OrderType.Project || input.CustomerId.Value == Guid.Empty)
            return DerivationResult.Empty;

        var options = await db.Projects
            .Where(x => x.CustomerId == input.CustomerId && x.IsOpen)
            .OrderBy(x => x.Name)
            .Select(x => new Option(x.Id.Value, x.Name))
            .ToListAsync(ct);

        return DerivationResult.Empty.AddOptions(nameof(CreateOrder.Input.ProjectId), options);
    }

    [ServerDerivation("orders.create.customer-state")]
    [DependsOn(nameof(CreateOrder.Input.CustomerId))]
    public static async Task<DerivationResult> CustomerState(
        CreateOrder.Input input, DerivationContext context, ErpDbContext db, CancellationToken ct)
    {
        if (input.CustomerId.Value == Guid.Empty) return DerivationResult.Empty;

        var check = await OrderRules.CustomerCanReceiveOrder(
            input.CustomerId, context.Operation.TenantId, db, ct);
        if (check.IsError)
            return DerivationResult.From(check, target: nameof(CreateOrder.Input.CustomerId));

        var customer = await db.Customers.WithInherited(db, context.Operation.TenantId)
            .Where(x => x.Id == input.CustomerId)
            .Select(x => new { x.CreditBlocked, x.VisitAddress })
            .SingleAsync(ct);

        var result = DerivationResult.Empty;
        if (customer.CreditBlocked)
            result = result.AddWarning(CustomerFindings.CreditBlocked);

        return result.Suggest(nameof(CreateOrder.Input.WorkAddress), customer.VisitAddress.Value);
    }
}
```

The small print that keeps this honest: an unpicked `CustomerId` is its struct default, so the guard tests `Value == Guid.Empty`; `Option` is a wire value plus a label; `[DependsOn]` names members with `nameof` — the dependency list is what the form runtime batches and debounces on. Note `CustomerState` reuses `OrderRules.CustomerCanReceiveOrder` — the same rule the operation enforces authoritatively in Step 2. The form warns early; the transaction decides finally.

**Derived:** the dependency graph (`ProjectId` visibility/requiredness ← `OrderType`; `ProjectId` options ← `CustomerId`, `OrderType`; `WorkAddress` suggestion ← `CustomerId`), batched `/api/forms/{id}/resolve` evaluation with client-side debouncing and stale-response rejection, and the same preflight surface for MCP (Step 8). *(Designed, not built: dependency-cycle detection and a `FORM001` diagnostic for rules that contradict each other.)*

---
