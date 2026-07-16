# Step 4 — Bindings: one per boundary that differs *(BUILT)*

Bindings are declared on the model builder in the host's composition root — registration and
implementation one screen apart, wire ids explicit (docs/29):

```csharp
// samples/erp/Program.cs (the composition root)

.Form<CreateOrder.Input>("web.orders.create", "orders.create", form =>
{
    form.Field(x => x.CustomerId).Renderer("customer-picker");
    form.Field(x => x.OrderType);
    form.Field(x => x.ProjectId)
        .VisibleWhen(x => x.OrderType == OrderType.Project)     // portable Px — client-side
        .RequiredWhen(x => x.OrderType == OrderType.Project);
    form.Field(x => x.WorkAddress)
        .OnSourceChange(DependentValuePolicy.RecomputeIfUntouched);  // server suggestion policy
    form.Field(x => x.Description);
    form.Field(x => x.RequestedDate);
    form.Field(x => x.EstimatedTotal).Renderer("money");
    form.Extensions();                     // tenant fields splice in here (Step 9)
})
```

This form DEVIATES from its record (a custom renderer, Px visibility, a suggestion policy), so
it declares its decisions. A form with nothing to decide declares nothing —
`.Form<CreateCustomer.Input>("web.customers.create", "customers.create")` binds every input
field in record order: **the record IS the form** (docs/32 D-P6). A `mobile.*` twin narrowing
this form (`BasedOn` + hide/re-render) and inline context display from a lookup's result are
designed (docs/05) but not yet built — today a second surface declares its own binding.

The frontend, in its entirety, for both apps:

```tsx
<OperationForm form="web.orders.create" />
```

The component takes the FORM id — the operation, the fields, the rules all come from the manifest entry the id names. The generic runtime renders fields in declared order with registered renderers (`customer-picker`, `money`) and semantic-type defaults, evaluates portable rules locally as the user types (project fields appear the instant "Project" is selected), calls batched server resolution for contextual derivations (options load, warnings appear, the address gets suggested), and disables submit while blocking findings exist. Pixels — layout, density, components — belong to the app's registered renderers, never to the server.

---
