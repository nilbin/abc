# 20 — Tutorial: A Complete Feature, End to End

This walkthrough builds a complete **Work Orders** feature for a fictional field-service company. It shows every file a developer writes and, after each step, what the framework derives from it. It is written as if the framework exists; treat it as the executable specification of the developer experience — if implementing a phase makes this document untrue, the implementation is wrong.

**The scenario.** Customers call in service jobs. An order is created for a customer, optionally linked to a project, carries a work address and description, and is eventually completed. Back-office staff use the web app, technicians use mobile, the Fortnox integration imports orders, and agents create orders over MCP. One tenant wants a "Machine serial number" field on orders.

**What we will write:** one domain file, four feature folders, one integration mapping. **What we will not write:** controllers, DTOs, validators, API clients, form schemas, grid schemas, MCP wrappers, audit code, or conflict handling.

```
Product.Domain/Orders/Order.cs
Product.Application/Orders/
  Create/       Operation.cs  Derivations.cs  Bindings.cs  Tests.cs
  EditDetails/  Operation.cs  Bindings.cs     Tests.cs
  Complete/     Operation.cs  Tests.cs
  List/         View.cs       Bindings.cs     Tests.cs
Product.Integrations/Fortnox/ImportFortnoxOrder.cs
Product.Application/locales/  sv.json  en.json
```

---

## Step 1 — Domain state

Plain C#. Semantic value types carry intrinsic meaning once; everything downstream reuses it. Note what is *absent*: no display text anywhere — labels and messages resolve by key from the locale files ([21-localization.md](21-localization.md)), and a hardcoded string in a display position is build error `L10N000`.

```csharp
// Product.Domain/Orders/Order.cs

public readonly record struct OrderNumber(string Value);

[Multiline, MaxLength(1000)]
public readonly record struct OrderDescription(string Value);

public enum OrderType { Service, Project }

public enum OrderStatus { Open, Completed, Cancelled }

public sealed class Order : IExtensible
{
    public OrderId Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public OrderNumber Number { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public OrderType Type { get; private set; }
    public ProjectId? ProjectId { get; private set; }
    public Address WorkAddress { get; private set; }
    public OrderDescription Description { get; private set; }
    public DateOnly? RequestedDate { get; private set; }
    public Money? EstimatedTotal { get; private set; }
    public OrderStatus Status { get; private set; }
    public ExtensionData Extensions { get; private set; }

    public static Order Create(
        OrderNumber number, CustomerId customerId, OrderType type,
        ProjectId? projectId, Address workAddress, OrderDescription description,
        DateOnly? requestedDate, Money? estimatedTotal) { /* ... */ }

    public void ChangeDescription(OrderDescription description)
        => Description = description;

    public Result Complete()
    {
        if (Status == OrderStatus.Completed)
            return OrderErrors.AlreadyCompleted;
        if (Status == OrderStatus.Cancelled)
            return OrderErrors.CannotCompleteCancelled;

        Status = OrderStatus.Completed;
        return Result.Success();
    }
}
```

Domain errors are **finding factories** — a stable code, no prose; the code doubles as the message key:

```csharp
public static class OrderErrors
{
    public static readonly FindingFactory AlreadyCompleted =
        Finding.Error("orders.already-completed");
    public static readonly FindingFactory CannotCompleteCancelled =
        Finding.Error("orders.cannot-complete-cancelled");
    public static readonly FindingFactory InvalidCustomer =
        Finding.Error("orders.invalid-customer");
}
```

Every word a human will read lives in the locale files, per culture, reviewed like code:

```jsonc
// locales/sv.json
{
  "orders.order.number": "Ordernummer",
  "orders.order.description": "Arbetsbeskrivning",
  "orders.already-completed": "Ordern är redan slutförd.",
  "orders.invalid-customer": "Den valda kunden kan inte ta emot ordrar."
}
// locales/en.json — same keys; gaps are CI warnings with a completeness report
```

Operation inputs, view columns, and forms all inherit these keys by convention — `CreateOrder.Input.Description` displays `orders.order.description`'s text with zero authoring.

EF Core mapping is ordinary EF Core; `Tam.EntityFrameworkCore` conventions handle semantic value conversions, and the JSONB `Extensions` column comes from `IExtensible`:

```csharp
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.Property(x => x.Description).HasMaxLength(1000);   // matches [MaxLength(1000)]
        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        // TenantId query filter, concurrency token, Extensions JSONB: applied by convention
    }
}
```

> **Consistency is verified, not assumed.** If the column were `HasMaxLength(500)`, the build fails:
> `DB001: CreateOrder.Input.Description permits 1,000 characters but Order.Description is persisted as varchar(500).`

---

## Step 2 — The create operation

The only way an order comes into existence, no matter who calls.

```csharp
// Product.Application/Orders/Create/Operation.cs

[Operation("orders.create")]
[Authorize("orders.create")]
[AcceptsExtensions(For<Order>())]
public static partial class CreateOrder
{
    public sealed record Input(
        CustomerId CustomerId,
        OrderType OrderType,
        ProjectId? ProjectId,
        Address WorkAddress,
        OrderDescription Description,
        DateOnly? RequestedDate = null,
        Money? EstimatedTotal = null) : IExtensibleInput;

    public sealed record Output(OrderId OrderId, OrderNumber Number);

    public static async Task<Result<Output>> Execute(
        Input input,
        OperationContext context,
        IOrderNumberSequence numbers,          // generator-wired from DI
        CancellationToken ct)
    {
        var customerCheck = await OrderRules.CustomerCanReceiveOrder(
            input.CustomerId, context.Db, ct);
        if (customerCheck.IsError)
            return customerCheck;

        if (input.OrderType == OrderType.Project)
        {
            var projectCheck = await OrderRules.ProjectBelongsToCustomer(
                input.ProjectId, input.CustomerId, context.Db, ct);
            if (projectCheck.IsError)
                return projectCheck;
        }

        var order = Order.Create(
            await numbers.NextAsync(ct),
            input.CustomerId, input.OrderType, input.ProjectId,
            input.WorkAddress, input.Description,
            input.RequestedDate, input.EstimatedTotal);

        context.Db.Orders.Add(order);

        return new Output(order.Id, order.Number);
    }
}
```

The business rules live once, shared with derivations (Step 3):

```csharp
public static class OrderRules
{
    public static async Task<Result> CustomerCanReceiveOrder(
        CustomerId customerId, AppDbContext db, CancellationToken ct)
    {
        var c = await db.Customers.Where(x => x.Id == customerId)
            .Select(x => new { x.IsActive }).SingleOrDefaultAsync(ct);
        return c is { IsActive: true } ? Result.Success() : OrderErrors.InvalidCustomer;
    }
    // ProjectBelongsToCustomer(...) similar
}
```

**Derived from this file alone** — no further code:

| Artifact | Result |
| --- | --- |
| HTTP endpoint | `POST /api/operations/orders.create` |
| OpenAPI + JSON Schema | Input/Output schemas from the records; nullability = requiredness |
| TypeScript client | `client.orders.create(input): Promise<OperationResult<Output>>` — fully typed |
| MCP tool | `orders.create` with the same schema (Step 8) |
| Pipeline | authorization, transaction, structural validation, audit entry, idempotency, correlation, `TenantId` stamping |
| Permission catalogue | `orders.create` appears in the manifest; unknown permission strings are build errors |

The wire envelope every caller gets back:

```json
{
  "output": { "orderId": "ord_9f3k", "number": "2026-01415" },
  "findings": [],
  "effects": [ { "type": "entity-created", "entity": "orders.order", "id": "ord_9f3k" } ],
  "newVersion": 1,
  "auditReference": "aud_77b21"
}
```

---

## Step 3 — Derivations: reactive form behavior, written once

Three facts about order creation are interactive: project fields only matter for project orders; project options depend on the chosen customer; picking a customer should validate them and suggest an address.

```csharp
// Product.Application/Orders/Create/Derivations.cs

public static partial class CreateOrderDerivations
{
    // Portable: lowered to the expression AST, evaluated client-side instantly
    [PortableDerivation]
    public static bool ProjectVisible(CreateOrder.Input input)
        => input.OrderType == OrderType.Project;

    [PortableDerivation]
    public static bool ProjectRequired(CreateOrder.Input input)
        => input.OrderType == OrderType.Project;

    // Contextual: needs the database, runs server-side, batched + debounced
    [ServerDerivation("orders.create.available-projects")]
    [DependsOn<CreateOrder.Input>(
        nameof(CreateOrder.Input.CustomerId),
        nameof(CreateOrder.Input.OrderType))]
    public static async Task<DerivationResult> AvailableProjects(
        CreateOrder.Input input, DerivationContext context, CancellationToken ct)
    {
        if (input.OrderType != OrderType.Project || input.CustomerId is null)
            return DerivationResult.Empty;

        var options = await context.Db.Projects
            .Where(x => x.CustomerId == input.CustomerId && x.IsOpen)
            .Select(x => Option.Create(x.Id, x.Name))
            .ToListAsync(ct);

        return DerivationResult.Options(nameof(CreateOrder.Input.ProjectId), options);
    }

    [ServerDerivation("orders.create.customer-state")]
    [DependsOn<CreateOrder.Input>(nameof(CreateOrder.Input.CustomerId))]
    public static async Task<DerivationResult> CustomerState(
        CreateOrder.Input input, DerivationContext context, CancellationToken ct)
    {
        if (input.CustomerId is null)
            return DerivationResult.Empty;

        var check = await OrderRules.CustomerCanReceiveOrder(input.CustomerId, context.Db, ct);
        if (check.IsError)
            return DerivationResult.From(check, target: nameof(CreateOrder.Input.CustomerId));

        var customer = await context.Db.Customers
            .Where(x => x.Id == input.CustomerId)
            .Select(x => new { x.CreditBlocked, x.VisitAddress })
            .SingleAsync(ct);

        var result = DerivationResult.Empty;
        if (customer.CreditBlocked)
            result = result.AddWarning(CustomerFindings.CreditBlocked);

        return result.Suggest(nameof(CreateOrder.Input.WorkAddress), customer.VisitAddress);
    }
}
```

Note `CustomerState` reuses `OrderRules.CustomerCanReceiveOrder` — the same rule the operation enforces authoritatively in Step 2. The form warns early; the transaction decides finally.

**Derived:** the dependency graph (`ProjectId` visibility/requiredness ← `OrderType`; `ProjectId` options ← `CustomerId`, `OrderType`; `WorkAddress` suggestion ← `CustomerId`), cycle checking at build time, batched `/resolve` evaluation with debouncing, stale-response rejection, and the same preflight surface for MCP. If `ProjectRequired` referenced a field that `ProjectVisible` hides in all cases: `FORM001` at build time.

---

## Step 4 — Bindings: one per boundary that differs

```csharp
// Product.Application/Orders/Create/Bindings.cs

[FormBinding<CreateOrder.Input>("web.orders.create")]
public static partial class CreateOrderForm
{
    public static void Configure(FormBuilder<CreateOrder.Input> form)
    {
        form.Operation(CreateOrder.Definition);

        form.Field(x => x.CustomerId).Renderer("customer-picker");

        form.Context(CustomerSummary.Definition,
            x => new CustomerSummary.Query(x.CustomerId));
        form.Show<CustomerSummary.Result>(x => x.Name);
        form.Show<CustomerSummary.Result>(x => x.Phone);

        form.Field(x => x.OrderType);
        form.Field(x => x.ProjectId);          // visibility/options come from derivations

        form.Field(x => x.WorkAddress)
            .SuggestFrom<CustomerSummary.Result>(x => x.VisitAddress)
            .OnSourceChange(DependentValuePolicy.RecomputeIfUntouched);

        form.Field(x => x.Description);
        form.Field(x => x.RequestedDate);
        form.Field(x => x.EstimatedTotal);

        form.Extensions();                     // tenant fields splice in here (Step 9)
    }
}

[FormBinding<CreateOrder.Input>("mobile.orders.create")]
public static partial class MobileCreateOrderForm
{
    public static void Configure(FormBuilder<CreateOrder.Input> form)
    {
        form.BasedOn(CreateOrderForm.Definition);
        form.HideContextField<CustomerSummary.Result>(x => x.Phone);
        form.Hide(x => x.EstimatedTotal);      // office concern, not field concern
        form.Renderer(x => x.WorkAddress, "gps-assisted-address");
    }
}
```

The frontend, in its entirety, for both apps:

```tsx
<OperationForm operation="orders.create" />
```

The generic runtime reads the effective manifest: it renders fields in declared order with semantic-type renderers (`customer-picker`, `address`, `money`), evaluates portable rules locally as the user types (project fields appear the instant "Project" is selected), calls batched server resolution on blur for contextual derivations (options load, warnings appear, address gets suggested), and disables submit while blocking findings exist. Pixels — layout, density, components — belong to the app's registered renderers, never to the server.

---

## Step 5 — The list: a view and its grid

```csharp
// Product.Application/Orders/List/View.cs

[View("orders.list")]
[Authorize("orders.read")]
public static partial class OrderList
{
    // Status/Type filtering is mechanical — declared below, composed by the framework (D7).
    // The Query record carries only authored logic the framework cannot derive.
    public sealed record Query(SearchText? Search = null);

    public sealed record Result(
        OrderId Id,
        OrderNumber Number,
        CustomerName CustomerName,
        OrderType Type,
        OrderStatus Status,
        DateOnly? RequestedDate,
        long Version);

    public static IQueryable<Result> Execute(Query query, AppDbContext db)
    {
        return db.Orders.AsQueryable()
            .Join(db.Customers, o => o.CustomerId, c => c.Id, (o, c) => new Result(
                o.Id, o.Number, c.Name, o.Type, o.Status, o.RequestedDate, o.Version))
            .SearchOn(query.Search, x => x.Number, x => x.CustomerName);
    }

    public static void Capabilities(ViewCapabilities<Result> caps)
    {
        caps.Sortable(x => x.Number, x => x.CustomerName, x => x.RequestedDate);
        caps.Filterable(x => x.Status, x => x.Type);
        caps.DefaultSort(x => x.Number, descending: true);
    }
}
```

Declared capabilities are the contract — and the implementation: `Filterable(Status)` makes the framework compose the SQL predicate and the grid render the filter control, with no further code (D7). A binding sorting on an undeclared field is `VIEW001` at build time, and `Tam.Testing` executes every declared capability against real PostgreSQL in CI — untranslatable LINQ becomes a red test, not a production 500. Tenant custom fields filter the same way (`?ext.machineSerialNumber=…`) — necessarily mechanically, since a runtime-defined field can never appear in a compiled Query record.

```csharp
// Product.Application/Orders/List/Bindings.cs

[GridBinding<OrderList.Query, OrderList.Result>("web.orders.list")]
public static partial class OrdersGrid
{
    public static void Configure(GridBuilder<OrderList.Result> grid)
    {
        grid.View(OrderList.Definition);

        grid.Column(x => x.Number);
        grid.Column(x => x.CustomerName);
        grid.Column(x => x.Type);
        grid.Column(x => x.Status);
        grid.Column(x => x.RequestedDate);

        grid.Extensions();                              // tenant columns (Step 9)

        grid.RowAction(CompleteOrder.Definition);       // availability batched per page
        grid.ToolbarAction(CreateOrder.Definition);
    }
}
```

Frontend: `<ViewGrid view="web.orders.list" />`. Paging, sorting, filtering, search, row actions with per-row availability, toolbar actions gated by the actor's permissions — all from the manifest.

---

## Step 6 — Editing: partial, conflict-safe

```csharp
// Product.Application/Orders/EditDetails/Operation.cs

[Operation("orders.edit-details")]
[Authorize("orders.edit")]
[AcceptsExtensions(For<Order>())]
public static partial class EditOrderDetails
{
    public sealed record Input(
        OrderId OrderId,
        Change<OrderDescription>? Description = null,
        Change<DateOnly?>? RequestedDate = null,
        Change<Address>? WorkAddress = null,
        Change<Money?>? EstimatedTotal = null) : IExtensibleInput;

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, CancellationToken ct)
    {
        var order = await context.Db.Orders.LoadForUpdateAsync(input.OrderId, ct);

        if (order.Status != OrderStatus.Open)
            return OrderErrors.NotEditable;

        var merge = context.Changes.Apply(order, input);   // three-way, semantic equality
        if (merge.HasConflicts)
            return merge.ToConflictResult();

        return new Output(order.Version + 1);
    }
}
```

The handler states the *business* precondition (only open orders are editable). Everything mechanical — dirty detection, three-way merge with row lock, semantic equality per value type, conflict shaping, field-level audit — is the pipeline's job.

Two dispatchers edit order `2026-01415` concurrently. Anna changes the description; Björn changed the requested date a moment earlier. Anna's submission:

```json
{
  "orderId": "ord_9f3k",
  "changes": {
    "description": { "original": "Repair pump", "value": "Replace pump" }
  }
}
```

Current `description` still equals Anna's original → her change applies cleanly even though the row version moved. Had both edited the description, she'd get — instead of an exception page —

```json
{
  "findings": [ {
    "code": "concurrency.field-conflict", "severity": "error",
    "targets": ["description"], "blocksSubmission": true
  } ],
  "conflicts": [ {
    "field": "description",
    "originalValue": "Repair pump",
    "currentValue": "Overhaul pump assembly",
    "submittedValue": "Replace pump"
  } ]
}
```

— which the form runtime renders as *keep current / use mine / review*.

The edit form binding (`EditDetails/Bindings.cs`) mirrors Step 4 and loads its initial values from a detail view; ~15 lines, omitted here.

---

## Step 7 — Completing: an intent, not an edit

```csharp
// Product.Application/Orders/Complete/Operation.cs

[Operation("orders.complete")]
[Authorize("orders.complete")]
public static partial class CompleteOrder
{
    public sealed record Input(OrderId OrderId, long ExpectedVersion);
    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, CancellationToken ct)
    {
        var order = await context.Db.Orders.LoadForUpdateAsync(
            input.OrderId, input.ExpectedVersion, ct);

        var result = order.Complete();
        if (result.IsError)
            return result;

        return new Result<Output>(new Output(order.Version + 1))
            .Effect(new OrderCompleted(order.Id, order.CustomerId));
    }
}
```

No `Change<T>` here, deliberately: status is consequential state, so it moves only through this intent (`EDIT001` fires if anyone exposes `Status` through a generic edit). The `OrderCompleted` effect drives the outbox event, cache invalidation, the grid's live refresh (decision D5), and the audit trail — none of it written here. Note there is no extension channel either: tenant fields never ride on intents.

---

## Step 8 — What the machine callers see

The same feature, no extra code:

**HTTP** — `POST /api/operations/orders.create`, `orders.edit-details`, `orders.complete`; `GET /api/views/orders.list?status=Open&sort=-number&page=2`. OpenAPI documents all of it.

**TypeScript** —

```ts
const result = await client.orders.create({
  customerId, orderType: "Service",
  workAddress, description: "Replace pump",
});
// result.output?.number — typed end to end
```

**MCP** — an agent asked to "create a project order for Acme's pump replacement":

```
→ tool: orders.create.resolve   { "customerId": "cus_acme", "orderType": "Project" }
← { "missingRequired": ["projectId", "workAddress", "description"],
    "fields": { "projectId": { "options": [
        { "value": "prj_11", "label": "Pump refurbishment 2026" },
        { "value": "prj_14", "label": "Annual service agreement" } ] },
      "workAddress": { "suggested": { "street": "Industrigatan 4", "city": "Västerås" } } },
    "findings": [ { "code": "customers.credit-blocked", "severity": "warning",
                    "message": "The customer is currently credit blocked." } ] }

→ tool: orders.create   { ...full input, "idempotencyKey": "agent-run-88f2" }
← { "output": { "orderId": "ord_a112", "number": "2026-01416" }, "auditReference": "aud_79c02" }
```

The agent hit the same derivations, the same validation, the same audit trail as the web form — `resolve` is the form runtime's endpoint wearing a tool schema. The `message` text arrived resolved in the connection's culture from the same locale catalogs the web form uses; the `code` is what the agent branches on. There is no agent-specific business logic anywhere in the feature.

---

## Step 9 — The tenant adds a custom field. Nobody deploys anything.

A tenant administrator (or an agent on their behalf) calls a framework operation — the registry is itself just operations:

```json
POST /api/operations/extensions.define-field
{
  "target": "orders.order",
  "key": "machineSerialNumber",
  "type": "text",
  "labels": { "sv": "Maskinserienummer", "en": "Machine serial number" },
  "descriptions": {
    "sv": "Serienummer för den servade maskinen, från typskylten.",
    "en": "Serial number of the serviced machine, from the type plate."
  },
  "constraints": { "maxLength": 40 },
  "placement": { "after": "description", "bindingClasses": ["web", "mobile"] },
  "permissions": { "write": ["dispatcher", "technician"] }
}
```

The registry runs its `EXT###` checks (key collision, visibility cycles, orphaned options — the compiler's rules, hosted at runtime), activates the field, and bumps the tenant's manifest revision. Because every binding in this tutorial opted in with `form.Extensions()` / `grid.Extensions()`, the field is **immediately**:

- an input on the web and mobile create/edit forms, after Description, validated to 40 characters, rendered by the standard `text` renderer;
- a column available in the orders grid, filterable (JSONB-translated; promotable to an expression index if it gets hot);
- carried in `orders.edit-details` submissions as `"extensions": { "machineSerialNumber": { "original": null, "value": "MX-55012" } }` — same `Change<T>`, same three-way merge, same conflicts;
- in the audit trail, field-level, like any compiled field;
- in the MCP tool schema with the admin's description — agents can read and write it with elicitation;
- mappable from Fortnox by field id, ownership-checked at configuration time.

What it can never do: gate `orders.complete`, appear on an intent operation, or otherwise steer compiled business decisions — the analyzer holds that line (see [15-extensibility.md](15-extensibility.md)). When serial numbers become load-bearing (warranty lookups, say), the graduation scaffold promotes the field to a compiled property with a data migration.

---

## Step 10 — The integration is a mapping, not a sync engine

```csharp
// Product.Integrations/Fortnox/ImportFortnoxOrder.cs

[IntegrationBinding("fortnox.orders.import")]
public static partial class ImportFortnoxOrder
{
    public static void Configure(
        IntegrationBuilder<FortnoxOrder, CreateOrder.Input> integration)
    {
        integration.Into(CreateOrder.Definition);

        integration.Map(t => t.CustomerId,   s => ResolveCustomer(s.CustomerNumber));
        integration.Map(t => t.OrderType,    s => OrderType.Service);
        integration.Map(t => t.WorkAddress,  s => s.DeliveryAddress);
        integration.Map(t => t.Description,  s => s.Description);

        integration.IdempotencyKey(s => s.DocumentNumber);
    }
}
```

Imported orders execute `orders.create` — same authorization (as the integration principal), same rules, same findings, same audit. Inbox, retries, dead-lettering, replay, and reconciliation come from `Tam.Integrations`. Forgetting to map a required field is `INT001` at build time, not a support ticket.

**Shipped as a plugin (implemented — samples/fortnox).** In the running system this integration is not host code at all — it lives in a `fortnox` plugin, activation- and entitlement-gated, mapped to `POST /api/integrations/fortnox.orders.import`. The plugin references no host CLR type: it maps to the `orders.create` *wire* contract and resolves the vendor customer name through the host's `customers.lookup` *view* (as the request's actor), and the inbox stores each source row so a customer created later recovers the failed import with no re-send. That a whole external-integration capability is a removable, per-tenant-priced plugin — over the same seams as fields and gates — is the extensibility thesis at full stretch.

---

## Step 11 — Tests exercise the contract, not the plumbing

```csharp
// Product.Application/Orders/Create/Tests.cs  (representative cases)

public sealed class CreateOrderTests : OperationTest
{
    [Fact]
    public async Task Creates_order_for_active_customer()
    {
        var customer = await Given.ActiveCustomer();

        var result = await Execute(CreateOrder.Definition, ValidInput(customer.Id));

        result.ShouldSucceed();
        result.ShouldHaveEffect<EntityCreated>(e => e.Entity == "orders.order");
        await Audit.ShouldContain(result.AuditReference);
    }

    [Fact]
    public async Task Rejects_inactive_customer()
    {
        var customer = await Given.InactiveCustomer();

        var result = await Execute(CreateOrder.Definition, ValidInput(customer.Id));

        result.ShouldFailWith("orders.invalid-customer");
    }

    [Fact]
    public async Task Concurrent_edits_to_different_fields_merge()
    {
        var order = await Given.OpenOrder();

        await Execute(EditOrderDetails.Definition, ChangeRequestedDate(order));
        var result = await Execute(EditOrderDetails.Definition, ChangeDescription(order));

        result.ShouldSucceed();     // three-way merge, no false conflict
    }
}
```

The test host runs the real pipeline (authorization, transaction, merge, audit) against a real database, so what's green here is what's true in production. Binding snapshot tests pin the manifest output; capability tests execute every declared sort/filter.

---

## Step 12 — Six months later: change impact

A developer adds `CustomerReference` to `CreateOrder.Input` as a required field. The build answers before any human review does:

```
Added CreateOrder.Input.CustomerReference (required)

✓ HTTP + OpenAPI schema updated
✓ Web create form: field added after Description
✓ Mobile create form: field added (inherits from web binding)
✓ MCP tool schema updated
✓ TypeScript client regenerated
– No database migration required (input-only, mapped to existing column)
✗ INT001 fortnox.orders.import does not map required field CustomerReference
✗ MANIFEST: non-additive change vs. release baseline — requires baseline approval (D4)
```

Two red lines, both at compile/CI time: the Fortnox mapping must be extended, and the compatibility baseline must be consciously re-approved because a required input is a breaking change for external callers. Nothing reaches production surprised.

---

## Step 13 — A partner ships a plugin *(implemented — [22-plugins.md](22-plugins.md), decision D8; running in samples/inspect)*

Norrservice's certification partner sells an inspection-checklist capability. It arrives as a NuGet package, and the host application adds one line:

```csharp
model.AddPlugin<InspectionPlugin>();
```

Inside the package, the same five concepts as everywhere else — a `ChecklistTemplate` entity, `inspect.checklists.*` operations and views, forms and grids, embedded sv/en locales. Three things make it a *plugin* rather than a copy of the host's patterns:

```csharp
[TamPlugin("inspect")]                                  // permanent namespace; PLG001 enforces it
public sealed class InspectionPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.Model.AddDiscovered();                   // the plugin's own compile-time discovery

        // A packaged field on the HOST's entity — same channel as tenant custom fields,
        // compiled origin, collision-proof key "inspect.requiresInspection", label from the
        // plugin's own locale files. Addressed by WIRE key: a plugin references the host's
        // contract, never its assembly.
        plugin.ExtensionField("order", "requiresInspection", "boolean");

        // A declared precondition on the HOST's operation — visible in the manifest as
        // orders.complete.gatedBy: ["inspect"] and in the impact report. The gate reads the
        // wire input and the plugin's OWN data (via the ITamDb seam), never host CLR types.
        plugin.Gate("orders.complete", async (gate, ct) =>
        {
            var orderId = gate.Input.GetProperty("orderId").GetGuid();
            var db = ((ITamDb)gate.Services.GetService(typeof(ITamDb))!).Db;
            var blocked = await db.Set<Checklist>().AnyAsync(
                x => x.TenantId == gate.Context.TenantId.Value && x.OrderId == orderId && !x.Passed, ct);
            return blocked ? InspectFindings.ChecklistIncomplete : Result.Success();
        });

        // A reaction to a committed HOST effect — post-commit, off the outbox, in the
        // plugin's own scope. Completing an order opens its follow-up checklist.
        plugin.OnEffect("order-completed", async (effect, services, ct) => { /* create checklist */ });
    }
}
```

Because the packaged field rides the extension channel, it is already in every grid, form, audit trail, MCP schema, and D7 filter — none of that is plugin code. Because the gate is declared, `orders.complete` in the manifest now reads "gated by inspect", and the Step-12 impact report shows it when anyone touches `CompleteOrder`.

The tenant admin flips it on — `plugins.activate("inspect")` — an audited framework operation like any other. For tenants that haven't, the manifest simply omits everything: no nav entry, no MCP tools, no packaged field, HTTP 404 on `inspect.*`. Installing code was the vendor's deploy; enabling it was the tenant's click. And the trust line holds: the partner wrote C# through a compiler and a review; the *tenant* still authors only data — fields, roles, packages, and (later) custom objects and Px-bounded automation rules, per D8.

---

## Step 14 — Who is asking, and what have they paid for *(implemented — [24-subscriptions.md](24-subscriptions.md))*

Everything above assumed an actor with permissions. Two framework capabilities produce that actor and bound it, and neither is application code.

**Identity is the framework's own** (`Tam.Auth.OpenIddict`, behind the `IActorProvider` seam). The app calls `AddTamOpenIddict<ErpDbContext>()` and gets an embedded OpenIddict token server: humans sign in with Authorization Code + PKCE on a framework-rendered, localized login page (no password grant — OAuth 2.1), pick their organization when they have more than one, and the SPA renews a 10-minute access token silently with a rotating refresh token. Agents and integrations use client credentials (a machine client acts as a same-named account, so an agent has roles and an audited identity like any human). Accounts are platform-global with per-tenant memberships ([26](26-tenancy-hierarchy-and-identity.md)); grants resolve *fresh from the membership's roles and policies on every request*, so revoking a role beats the token's lifetime. The token machinery is hardened for a public client holding its own tokens: one-time-use rotation with reuse detection (a replayed refresh token revokes its whole family), revocation on sign-out, entry validation so revocation bites immediately, and a pruned token store. Swap in any external IdP by replacing the provider; the rest of the framework never knows.

```csharp
builder.Services.AddTam<ErpDbContext>(model);
builder.Services.AddTamOpenIddict<ErpDbContext>();   // the whole auth story, one line
app.MapTamAuth();                                    // /connect/authorize + /connect/token + login/picker/invite pages
```

**Entitlements bound what that actor can reach** (docs/24). A tenant's subscription — plan, seats, plugin entitlements — is data a billing provider drives through `subscriptions.set-plan` (a service-actor operation, not the tenant admin: a Stripe webhook maps to one call). Two mechanical gates, both already the right place: `plugins.activate` refuses a plugin the plan doesn't entitle (a localized upsell finding, not a crash), and `users.define` refuses a new user past the seat ceiling. A tenant with no subscription row is simply the free plan, so the framework runs fully without any billing wired up. This is how the inspect and fortnox plugins of the last two steps become things a tenant *buys*: the marketplace adds the plugin id to `PluginEntitlements`, and activation starts succeeding — the framework never touches money, it reads one boolean.

The whole request now reads top to bottom as data: *the OpenIddict token names a user → the user's roles resolve to grants → the grants pass the operation's `[Authorize]` → the plan entitles the plugin the operation belongs to → the seat/entitlement gates hold → the pipeline runs → the audit row records the human's name.* Every arrow is a row in a table a tenant admin or a billing webhook can change without a deploy.

## Step 15 — Norrservice becomes a group *(implemented — [26](26-tenancy-hierarchy-and-identity.md) + [27](27-authorization-model.md))*

A year in, Norrservice buys a company in Kiruna. Nothing about Orders changes — what changes is *who stands where*.

**The tree is data.** An admin opens the Companies page (or calls `tenants.create`) and adds `nord` under `demo`; the registry stores a materialized path (`demo.nord`), and every data row keeps carrying exactly one `TenantId` — nothing is denormalized, so re-parenting a company later (`tenants.move`) rewrites paths in the tiny tenants table and touches no data row. Renames are `tenants.rename`; the whole lifecycle is operations, so it is authorized, audited and localized like everything else.

**Grants fan out; writes fan in.** Alva's one membership at `demo` carries `admin` with `cascade: true`, so she can *stand at* any descendant — the login picker and the header switcher offer the whole standable set, labeled by path ("Demo AB ▸ Norrservice Nord AB"). Standing at `nord` (an `X-Tam-Tenant` act-as header the client sets), everything she does — creates, audit rows, events, idempotency — lands in `nord`, because the whole request is re-bound to the target node. Reads widen only where a view asks for it: the group overview declares `subtree` (roll-up down the tree), the customer lookup declares `inherited` (group-owned reference data readable from every leaf), and everything else stays strictly node-local. The compiler enforces the sharp edge here: composing a widened source into a query without explicitly scoping the other side is a build error (TAM005), because EF's filter opt-out is query-wide.

**Access is two axes.** A role says what you can *do* (authored as access levels — `{"orders": "manage"}` — expanding to the permission atoms at load time, with `[Sensitive]` fields maskable down to reads *and* writes). An access policy says which *rows* you reach (`{"orders": "own"}`), attached per membership — the same `dispatcher` role means all orders for the agent and only-your-own for Didrik, without forking the role. Both registries are tenant data with admin pages (Roles, Access policies, Users, Companies), both validate against the compiled catalogue at define time, and both re-resolve per request.

**People arrive by invite.** `users.invite` creates the account and membership up front (the seat is consumed immediately, so the count the admin sees is the count that bills), mails a one-shot hashed link through the `ITamEmail` seam (the dev default logs it), and the invitee sets a password on a framework page. Inviting someone who already has an account elsewhere in the platform just adds the membership — one human, many tenants, one login.

The pattern of Steps 1–14 holds: the tree, the memberships, the roles, the policies, the invites are all *data behind operations* — no deploy moves a company, grants a scope, or seats a user.

## Step 16 — Approvals arrive as a plugin — and the domains never notice *(design — the next stress test for [22-plugins.md](22-plugins.md))*

Norrservice's group buys an add-on from a workflow vendor: purchase approvals. Orders above a
threshold need a manager's sign-off; time corrections need the team lead. The point of this step
is what it does **not** require: no change to `CreateOrder`, no change to any domain, and no
approval engine in the framework. Groups and workflows are exactly the things docs/28 keeps out
of core — so they arrive the way inspection checklists did in Step 13: as a package the tenant
activates.

What the vendor ships, using only plugin machinery that exists today:

```csharp
[TamPlugin("approvals")]
public sealed class ApprovalsPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.Model.AddDiscovered();
        // Its OWN aggregates, in the host database like inspect's checklists: ApprovalGroup
        // (nested if the vendor wants — nesting semantics are the PLUGIN's problem, the
        // framework never learns about groups), GroupMember, ApprovalRule (which operation ids
        // need sign-off, thresholds), ApprovalRequest (the parked envelope + its payload hash).
        // Plus approvals.* operations/views: request lists, approve/reject, group admin — each
        // an ordinary operation: authorized, audited, localized, in the manifest, an MCP tool.
        // OnEffect("approvals.requested") → ITamEmail: the approver gets a link. (Exists today.)
    }
}
```

The interesting part is the gate. Step 13's gate was declared against one known operation id;
approvals must intercept operation ids *the tenant configures at runtime*, park the request, and
later run it for real. Walking through one order:

1. Didrik submits `orders.create` for 180 000 kr. The approvals gate (running inside the
   pipeline, before the handler's effects commit) consults its `ApprovalRule` table: this
   operation + this threshold ⇒ sign-off required, and no approval ticket accompanies the
   request. The gate **parks the envelope** — operation id, wire body, actor, tenant, culture —
   as an `ApprovalRequest`, and blocks with `approvals.pending` (a localized finding the form
   renders as "submitted for approval", not as an error).
2. The team-lead group resolves (however the plugin defines resolution — flat, nested, quorum),
   `OnEffect` mails the approvers, and the pending request sits in the plugin's grid.
3. A lead runs `approvals.approve`. The plugin **replays the parked envelope** through the real
   pipeline — the same executor every caller uses — as the *original* actor, with the approval
   ticket attached. The gate sees a ticket whose payload hash matches the parked body (the same
   hash the idempotency machinery already computes) and passes; the order is created; the audit
   trail shows both facts: requested by Didrik, released by the lead.

The domain wrote none of this. `CreateOrder` still doesn't know approvals exist — for tenants
without the plugin, nothing changed; for tenants with it, the manifest says `orders.create` is
gated and the impact report shows it, exactly like Step 13.

**What this scenario proves — and the three seams it demands.** This is the sharpest stress test
of the plugin architecture so far, and it is honest about where today's machinery falls short:

1. **Config-driven gate targets.** Gates today bind to operation ids at plugin-compile time; the
   approvals gate must attach to whatever `ApprovalRule` rows say. The executor's gate loop needs
   a wildcard bucket ("run for every operation, decide from config") — small mechanically, but
   the manifest's `gatedBy` transparency becomes per-tenant, which the effective-manifest overlay
   already knows how to express.
2. **Parking must survive the rollback.** A blocking gate rolls the transaction back — that is
   its contract — but the `ApprovalRequest` row must COMMIT anyway, or nothing was parked. The
   pipeline needs a "park" outcome distinct from plain blocking: capture-and-commit the plugin's
   rows while discarding the domain's, likely via a separate unit of work for the gate's own
   writes (the same isolation the outbox dispatcher already practices).
3. **Sanctioned envelope replay.** Re-executing a stored envelope as the original actor is
   powerful and must be a framework seam, not an impersonation hack: replay is only valid for an
   envelope the pipeline itself parked, the payload hash must match, and the audit row carries
   dual attribution (initiator + releaser). The pieces exist — in-process execution, payload
   hashing, actor construction — but the seam must own the rules.

When these three land, "an opinionated nested-group approval system for existing domains, without
the domains knowing" stops being a slogan and becomes a package. That is the bar docs/22 set for
the plugin architecture; this step is how we will know it has been met.

---

## The tally

**Written by hand** (the only independently maintained facts):

| File | Approx. lines | Decisions it owns |
| --- | --- | --- |
| `Domain/Orders/Order.cs` | ~90 | invariants, value types, status transitions |
| `Create/Operation.cs` | ~55 | input/output, business rules, creation |
| `Create/Derivations.cs` | ~60 | project logic, customer feedback, address suggestion |
| `Create/Bindings.cs` | ~45 | field order, context display, mobile differences |
| `EditDetails/Operation.cs` + `Bindings.cs` | ~50 | which fields are patchable, editability rule |
| `Complete/Operation.cs` | ~25 | the intent and its effect |
| `List/View.cs` + `Bindings.cs` | ~65 | the query, capabilities, columns, actions |
| `Fortnox/ImportFortnoxOrder.cs` | ~20 | external mapping, idempotency |
| `locales/sv.json` + `locales/en.json` | ~30 | every word a human reads, per culture |
| Tests | ~150 | the contract |

**Derived, and therefore never drifting:** four HTTP endpoints and their OpenAPI, JSON Schemas, a typed TypeScript client, web + mobile create/edit forms with reactive behavior, a grid with paging/sorting/filtering/search and gated actions, three-way merge and structured conflicts, field-level audit, idempotency, outbox events, four MCP tools plus a resolve/elicitation surface, integration inbox/retry/replay, per-tenant custom-field participation across every one of those boundaries, and a compile-time report of what any change touches.

That ratio — roughly 550 handwritten lines owning every real decision, and everything mechanical derived — is the success criterion of [18-success-criteria.md](18-success-criteria.md) made concrete. When the implementation can run this document top to bottom, the framework is done enough to use.
