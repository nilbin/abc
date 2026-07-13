# 06 — Bindings

A binding exposes an operation or view at a system boundary.

Bindings include:

- HTTP
- Web
- Admin
- Mobile
- MCP
- External integrations
- Automation
- Scheduled jobs

Bindings own **only boundary-specific decisions**. They do not own business logic.

## Web form binding

```csharp
[FormBinding<CreateOrder.Input>("web.orders.create")]
public static partial class CreateOrderForm
{
    public static void Configure(
        FormBuilder<CreateOrder.Input> form)
    {
        form.Operation(CreateOrder.Definition);

        form.Field(x => x.CustomerId);

        form.Context(
            CustomerSummary.Definition,
            x => new CustomerSummary.Query(x.CustomerId));

        form.Show<CustomerSummary.Result>(
            x => x.Name);

        form.Show<CustomerSummary.Result>(
            x => x.Phone);

        form.Field(x => x.WorkAddress)
            .SuggestFrom<CustomerSummary.Result>(
                x => x.VisitAddress)
            .OnSourceChange(
                DependentValuePolicy.RecomputeIfUntouched);

        form.Field(x => x.Description);
    }
}
```

## Mobile binding

A mobile binding should reuse the operation while overriding only real interaction differences:

```csharp
[FormBinding<CreateOrder.Input>("mobile.orders.create")]
public static partial class MobileCreateOrderForm
{
    public static void Configure(
        FormBuilder<CreateOrder.Input> form)
    {
        form.BasedOn(CreateOrderForm.Definition);

        form.HideContextField<CustomerSummary.Result>(
            x => x.Phone);

        form.Renderer(
            x => x.WorkAddress,
            "gps-assisted-address");
    }
}
```

## Grid binding

```csharp
[GridBinding<OrderList.Query, OrderList.Result>(
    "admin.orders.list")]
public static partial class AdminOrdersGrid
{
    public static void Configure(
        GridBuilder<OrderList.Result> grid)
    {
        grid.View(OrderList.Definition);

        grid.Column(x => x.Number);
        grid.Column(x => x.CustomerName);
        grid.Column(x => x.Status);
        grid.Column(x => x.CreatedAt);

        grid.RowAction(CompleteOrder.Definition);
        grid.ToolbarAction(CreateOrder.Definition);
    }
}
```

The backend defines semantic columns and actions. Each frontend renderer decides:

- Desktop table
- Mobile cards
- Density
- Layout
- Component implementation
- Accessibility behavior

The backend must not send React component trees, CSS, or arbitrary layout scripts.

The principle is:

> **Server-defined semantics, client-defined presentation.**

## Extension fields in bindings

Bindings over extensible operations and views automatically include active tenant-defined fields according to the tenant field registry's placement metadata (`grid.Extensions()`, `form.Extensions()` opt-in, or opt-out per binding). Because the client runtime renders from field descriptors rather than generated components, tenant fields render with zero frontend changes. See [15-extensibility.md](15-extensibility.md).
