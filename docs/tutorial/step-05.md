# Step 5 — The list: a view and its grid *(BUILT)*

```csharp
// samples/erp/Features/Orders.cs

[View("orders.list")]
[Authorize("orders.read")]
[Widens("orders.read-all")]
[AcceptsExtensions(typeof(Order))]
public static class OrderList
{
    // Status/Type filtering is mechanical — declared below, composed by the framework (D7).
    // The Query record carries only authored logic the framework cannot derive.
    public sealed record Query(string? Search = null);

    // Init-property record: EF composes the projection server-side; the grid binds these fields.
    public sealed record Result
    {
        public OrderId Id { get; init; }
        public OrderNumber Number { get; init; }
        [LabelKey("labels.customer")]
        public CustomerName CustomerName { get; init; }
        public OrderType Type { get; init; }
        public OrderStatus Status { get; init; }
        public DateOnly? RequestedDate { get; init; }
        public decimal? EstimatedTotal { get; init; }
        [LabelKey("labels.company")]
        public string TenantId { get; init; } = "";
        public long Version { get; init; }
        public ExtensionData Extensions { get; init; } = new();
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        var orders = db.Orders.InScope(db, context.TenantId)   // explicit scope — see below
            .ScopedUnless(context, "orders.read-all", x => x.AssignedToActorId);
        if (!string.IsNullOrWhiteSpace(query.Search))
            orders = orders.Where(x =>
                ((string)(object)x.Number).Contains(query.Search!) ||
                ((string)(object)x.Description).Contains(query.Search!));

        return orders
            .Join(db.Customers.WithInherited(db, context.TenantId),   // group-shared lookup (Step 15)
                o => o.CustomerId, c => c.Id, (o, c) => new Result
            {
                Id = o.Id, Number = o.Number, CustomerName = c.Name, Type = o.Type,
                Status = o.Status, RequestedDate = o.RequestedDate,
                EstimatedTotal = o.EstimatedTotal, TenantId = o.TenantId,
                Version = o.Version, Extensions = o.Extensions,
            });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Number), nameof(Result.CustomerName), nameof(Result.RequestedDate))
        .Filterable(nameof(Result.Status), nameof(Result.Type), nameof(Result.CustomerName),
            nameof(Result.RequestedDate), nameof(Result.EstimatedTotal))
        .SubtreeRead(nameof(Result.TenantId))    // Steps 15/18: this same list is the group roll-up
        .DefaultSort(nameof(Result.Number), descending: true);
}
```

Two patterns here are enforced, not stylistic. **The paired-atom scope** (docs/28): `orders.read` is own-scoped by default, `[Widens("orders.read-all")]` declares the widening atom, and `.ScopedUnless(...)` applies it — an actor with only the base atom sees assigned orders, a dispatcher with the `-all` atom sees the board. Declaring the atom anywhere and *not* applying the scope on a view over that resource is build error `TAM006`, in both directions — fail-closed by construction. **Explicit scoping in compositions** (`InScope`, `WithInherited`): because one widened source strips EF's global filter from the whole query, composing without scoping the other side is build error `TAM005` (Step 15 explains the group semantics; at a single tenant these calls are the identity).

Search is authored logic — a hand-written `Where` — while declared capabilities are contract *and* implementation: `Filterable(Status)` makes the framework compose the SQL predicate and the grid render the filter control, with no further code (D7). A grid column naming a field the view doesn't produce fails model build with `VIEW001`; a sort request on an undeclared capability falls back to the declared default at runtime. `Tam.Testing`'s CapabilitySweep executes every declared capability against the real provider in CI ([Step 11](step-11.md)) — a declaration that cannot translate fails a named test, never a request. Tenant custom fields filter and sort the same mechanical way (`?ext.machineSerialNumber=…`) — necessarily, since a runtime-defined field can never appear in a compiled Query record.

```csharp
// samples/erp/Program.cs — beside the form above

.Grid<OrderList.Result>("web.orders.list", "orders.list", grid =>
{
    grid.Column(x => x.Number);            // explicit: this grid REORDERS (company second)
    grid.Column(x => x.TenantId);
    grid.Column(x => x.CustomerName);
    grid.Column(x => x.Type);
    grid.Column(x => x.Status);
    grid.Column(x => x.RequestedDate);
    grid.Column(x => x.EstimatedTotal);
    grid.Extensions();                     // tenant columns (Step 9)
    grid.RowAction("orders.complete");
    grid.ToolbarAction("orders.create");
})
```

Same convention as forms: a grid with nothing to decide declares nothing — every result field
becomes a column in record order, minus `id`/`version` row plumbing (docs/32 D-P6). This one
declares because it reorders and carries actions.

Frontend: `<ViewGrid grid="web.orders.list" />`. Paging, sorting, filtering, search, row actions with per-row availability, toolbar actions gated by the actor's permissions — all from the manifest.

---
