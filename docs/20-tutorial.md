# 20 — Tutorial: A Complete Feature, End to End

This walkthrough builds a complete **Work Orders** feature for a fictional field-service company. It shows every file a developer writes and, after each step, what the framework derives from it. It began life as the executable specification of the developer experience; the framework now exists, and the code samples in Steps 0–10 are lifted from the running sample (`samples/erp` and its sibling plugins) — if a sample here drifts from the source, the document is wrong. Where something is still design rather than code, the step says so: **BUILT** means verified against the source; *(designed, not built)* means the vision is stated but nothing enforces it yet.

**The scenario.** Customers call in service jobs. An order is created for a customer, optionally linked to a project, carries a work address and description, and is eventually completed. Back-office staff use the web app, technicians use mobile, the Fortnox integration imports orders, and agents create orders over MCP. One tenant wants a "Machine serial number" field on orders.

**What we will write:** one domain file, one feature file, a composition root, one integration plugin. **What we will not write:** controllers, DTOs, validators, API clients, form schemas, grid schemas, MCP wrappers, audit code, or conflict handling.

```
samples/erp/
  Erp.csproj             the build contract                    (Step 0)
  Db.cs                  the DbContext                         (Step 0)
  Program.cs             the composition root: model + host    (Steps 0, 4, 5, 18)
  Domain.cs              entities, value types, findings       (Step 1)
  Features/Orders.cs     operations, derivations, views        (Steps 2–7)
  Features/Customers.cs  the same pattern, smaller
  locales/               sv.json  en.json                      (Step 1)
samples/fortnox/         the Fortnox integration, as a plugin  (Step 10)
```

Note what is absent: no per-feature `Bindings.cs`. Bindings — forms, grids, nav, pages — are declared in the composition root, one screen apart from each other and from the pipeline that serves them (docs/29).

---

## Step 0 — A new host from nothing *(BUILT — `samples/erp`)*

Three files make a Tam host. Everything else in this tutorial lands inside them.

**The project file** is the build contract: the three framework references, the compiler as an analyzer, and the locale catalogs as analyzer inputs — which is what makes missing keys *build errors* rather than runtime surprises:

```xml
<!-- samples/erp/Erp.csproj  (Sdk="Microsoft.NET.Sdk.Web") -->
<ItemGroup>
  <ProjectReference Include="..\..\src\Tam.Core\Tam.Core.csproj" />
  <ProjectReference Include="..\..\src\Tam.EntityFrameworkCore\Tam.EntityFrameworkCore.csproj" />
  <ProjectReference Include="..\..\src\Tam.AspNetCore\Tam.AspNetCore.csproj" />
  <ProjectReference Include="..\..\src\Tam.Compiler\Tam.Compiler.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
<ItemGroup>
  <AdditionalFiles Include="locales/**/*.json" />
  <CompilerVisibleProperty Include="TamDefaultCulture" />
</ItemGroup>
<PropertyGroup>
  <TamDefaultCulture>sv</TamDefaultCulture>
</PropertyGroup>
```

A plugin project uses the same shape plus `<EmbeddedResource Include="locales/*.json" />` — its catalogs travel inside the package (Step 13).

**The DbContext** is ordinary EF Core wearing the framework's contracts:

```csharp
// samples/erp/Db.cs (trimmed)

public sealed class ErpDbContext(DbContextOptions<ErpDbContext> options, TenantScope tenantScope)
    : DbContext(options),
      Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.IDataProtectionKeyContext,
      ITenantScopeContext
{
    public string? CurrentTenantId => tenantScope.Current;             // drives the global filter
    public IReadOnlyList<string> TenantReadSet => tenantScope.ReadSet; // subtree reads (Step 15)
    public bool CrossTenantScope => tenantScope.AllTenants;            // sanctioned escalation (docs/33)

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Description).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        });

        // Plugin storage opts in here — one line per installed plugin that ships entities (Step 13).
        Inspect.InspectionPlugin.AddInspect(modelBuilder);

        modelBuilder.UseTam(Database.ProviderName);   // framework tables + semantic value conversions
        modelBuilder.UseTamOpenIddict();              // token/client storage for the auth server (Step 14)
        modelBuilder.ApplyTenantFilter(this);         // ONE tenant boundary for the entire model
    }
}
```

`ITenantScopeContext` is the whole tenancy handshake: the framework reads the ambient scope off the context, and `ApplyTenantFilter` turns it into a global query filter over every `ITenantScoped` entity — framework and domain alike — so isolation is a property of the model, not a `Where` clause fifty call sites have to remember. `IDataProtectionKeyContext` (one `DbSet`) keeps the secrets vault's key ring in the shared database (Step 10).

**The composition root** builds the model, then the host — in an order that matters:

```csharp
// samples/erp/Program.cs (trimmed)

var model = new TamModelBuilder()
    .DefaultCulture("sv")
    .Locales(Path.Combine(AppContext.BaseDirectory, "locales"))
    .AddDiscovered()   // compile-time discovery from Tam.Compiler — no runtime assembly scan
    .AddTamSystem()    // framework packages: users, roles, audit, extensions, plugins, …
    // …forms, grids, nav, pages: Steps 4, 5 and 18…
    .Build();

// Manifest export mode (D4): `dotnet run -- manifest [path]` for the CI baseline check.
if (TamManifestExport.TryHandle(model, args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ErpDbContext>(options =>
{
    options.UseSqlite(connectionString);   // or UseNpgsql + options.AddTamRls() — Step 15
    options.UseTamConventions();           // tenant auto-stamp — one call, never forgotten
});
builder.Services.AddTam<ErpDbContext>(model);
builder.Services.AddTamOpenIddict<ErpDbContext>(fallbackTenant: Seed.Tenant);   // Step 14

var app = builder.Build();
app.MapTamAuth();   // pins the request's active tenant right after authentication…
app.MapTam();       // …then everything else: /api/operations/*, /api/views/*,
                    // /api/forms/{id}/resolve, /api/manifest, /api/mcp, /api/events, /openapi.json
app.Run();
```

`MapTamAuth` before `MapTam`, always: the token's active-tenant claim must be honored before any endpoint touches data, so that every view, operation and lookup runs under the global tenant filter the DbContext declared above. And the manifest-export line is not ceremony — CI exports the manifest and diffs it against the committed baseline (`scripts/check_manifest.py`): additions are free, breaking changes (a removed field, a new required input) fail the build until a human commits the new baseline in the same PR (D4).

---

## Step 1 — Domain state *(BUILT)*

Plain C#. Semantic value types carry intrinsic meaning once; everything downstream reuses it. Note what is *absent*: no display text anywhere — labels and messages resolve by key from the locale files ([21-localization.md](21-localization.md)). *(Designed, not built: the `L10N000` analyzer rule that makes a hardcoded display string a build error. What the build enforces today is the reverse direction — `L10N001`, below.)*

```csharp
// samples/erp/Domain.cs

public readonly record struct OrderId(Guid Value);
public readonly record struct OrderNumber(string Value);
public readonly record struct CustomerName(string Value);

[Multiline, MaxLength(1000)]
public readonly record struct OrderDescription(string Value);

public readonly record struct Address(string Value);

public enum OrderType { Service, Project }

public enum OrderStatus { Open, Completed, Cancelled }

public sealed class Order : IExtensible, Tam.EntityFrameworkCore.IVersioned, Tam.EntityFrameworkCore.ITenantScoped
{
    private Order() { }   // EF materializes; everyone else goes through Create

    public OrderId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public OrderNumber Number { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public OrderType Type { get; private set; }
    public ProjectId? ProjectId { get; private set; }
    public Address WorkAddress { get; private set; }
    public OrderDescription Description { get; private set; }
    public DateOnly? RequestedDate { get; private set; }
    public decimal? EstimatedTotal { get; private set; }
    public OrderStatus Status { get; private set; }
    public string? AssignedToActorId { get; private set; }
    public long Version { get; set; }                       // IVersioned: stamped by the pipeline
    public ExtensionData Extensions { get; set; } = new();  // IExtensible: tenant fields (Step 9)

    public static Order Create(
        string tenantId, OrderNumber number, CustomerId customerId, OrderType type,
        ProjectId? projectId, Address workAddress, OrderDescription description,
        DateOnly? requestedDate, decimal? estimatedTotal) { /* assigns; Status = Open */ }

    public Result Complete()
    {
        if (Status == OrderStatus.Completed) return OrderErrors.AlreadyCompleted;
        if (Status == OrderStatus.Cancelled) return OrderErrors.CannotCompleteCancelled;
        Status = OrderStatus.Completed;
        return Result.Success();
    }
}
```

Three interface contracts, no base class: `ITenantScoped` (a plain `string TenantId` the pipeline stamps and the global filter scopes), `IVersioned` (a `long Version` the audit interceptor increments on every modification), `IExtensible` (the JSON-backed container tenant-defined fields live in). And note what money is: `EstimatedTotal` is a `decimal?` that a *binding* renders with `.Renderer("money")` (Step 4) — there is no `Money` CLR type, and search input is likewise a plain `string?` on the view's Query (Step 5). Semantic value types are for meaning the domain owns (`OrderNumber`, `OrderDescription`), not for formatting.

Domain errors are **finding factories** — a stable code, no prose; the code doubles as the message key:

```csharp
public static class OrderErrors
{
    public static readonly FindingFactory AlreadyCompleted = Finding.Error("orders.already-completed");
    public static readonly FindingFactory CannotCompleteCancelled = Finding.Error("orders.cannot-complete-cancelled");
    public static readonly FindingFactory InvalidCustomer = Finding.Error("orders.invalid-customer");
    public static readonly FindingFactory NotFound = Finding.Error("orders.not-found");
    public static readonly FindingFactory NotEditable = Finding.Error("orders.not-editable");
}
```

Every word a human will read lives in the locale files, per culture, reviewed like code:

```jsonc
// locales/sv.json (excerpt)
{
  "labels.number": "Ordernummer",
  "labels.description": "Arbetsbeskrivning",
  "labels.customer": "Kund",
  "enums.service": "Service",
  "enums.completed": "Slutförd",
  "operations.orders.create.title": "Ny order",
  "orders.already-completed": "Ordern är redan slutförd.",
  "orders.invalid-customer": "Den valda kunden kan inte ta emot ordrar."
}
// locales/en.json — same keys; gaps outside the default culture are CI warnings with a report
```

> **The locale key grammar.** Keys are derived, flat, and — for labels — global:
>
> - `labels.{kebab(member)}` — derived from the member name, so `labels.description` labels *every* `Description` field in the model. Sharing is the default; when the derived key would be wrong, `[LabelKey]` overrides it — `CreateOrder.Input.CustomerId` carries `[LabelKey("labels.customer")]` (Step 2) so the picker reads "Kund", not a raw "Customer id".
> - `enums.{kebab(value)}` — `OrderStatus.Completed` → `enums.completed`. Global, like labels.
> - `operations.{id}.title` — one title per operation; forms, modals and grid actions reuse it.
> - `nav.{id}` — navigation nodes (Step 18).
> - Findings resolve by their **code** (`orders.already-completed` above).
> - `ext.{key}` — tenant field labels, merged into the catalogs per tenant from the registry (Step 9); a plugin's packaged field ships its label as `ext.{pluginId}.{key}` in the plugin's own catalogs.
>
> The catalogs are analyzer inputs (`AdditionalFiles`, Step 0), so a key the model references but the default culture lacks is build error `L10N001` — in the IDE, before the app boots. Plugins embed `locales/*.json` as `EmbeddedResource` and register them with `plugin.LocaleDefaults()`; application locale files override plugin defaults.

EF Core mapping is ordinary EF Core, in the DbContext from Step 0 — no per-feature configuration classes:

```csharp
// Db.cs (from Step 0)
modelBuilder.Entity<Order>(b =>
{
    b.HasKey(x => x.Id);
    b.Property(x => x.Description).HasMaxLength(1000);   // matches [MaxLength(1000)]
    b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
});
```

Semantic value conversions, the tenant filter, the version stamp and the `Extensions` JSON column all come from Step 0's three calls (`UseTam`, `ApplyTenantFilter`, `UseTamConventions`) — never per entity. *(Designed, not built: a `DB001` diagnostic cross-checking `[MaxLength]` against the mapped column length; today the two `1000`s are kept honest by review.)*

---

## Step 2 — The create operation *(BUILT)*

The only way an order comes into existence, no matter who calls.

```csharp
// samples/erp/Features/Orders.cs

[Operation("orders.create")]
[Authorize("orders.create")]
[AcceptsExtensions(typeof(Order))]
public static class CreateOrder
{
    public sealed record Input(
        [property: LabelKey("labels.customer")] CustomerId CustomerId,
        OrderType OrderType,
        Address WorkAddress,
        OrderDescription Description,
        [property: LabelKey("labels.project")] ProjectId? ProjectId = null,
        DateOnly? RequestedDate = null,
        decimal? EstimatedTotal = null);

    public sealed record Output(OrderId OrderId, OrderNumber Number);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var customerCheck = await OrderRules.CustomerCanReceiveOrder(
            input.CustomerId, context.TenantId, db, ct);
        if (customerCheck.IsError) return customerCheck.As<Output>();

        if (input.OrderType == OrderType.Project)
        {
            var projectCheck = await OrderRules.ProjectBelongsToCustomer(
                input.ProjectId, input.CustomerId, db, ct);
            if (projectCheck.IsError) return projectCheck.As<Output>();
        }

        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;
        var sequence = await db.Orders.CountAsync(ct) + 1412;   // the demo's number series
        var order = Order.Create(
            context.TenantId.Value,
            new OrderNumber($"{year}-{sequence:D5}"),
            input.CustomerId,
            input.OrderType,
            input.OrderType == OrderType.Project ? input.ProjectId : null,
            input.WorkAddress,
            input.Description,
            input.RequestedDate,
            input.EstimatedTotal);

        db.Orders.Add(order);
        return new Output(order.Id, order.Number);
    }
}
```

The shape to notice: the handler's parameter list *is* its dependency declaration — the wire input, the ambient `OperationContext` (actor, tenant, culture, idempotency key; **no `.Db`**, no service locator), the application's own `ErpDbContext` injected like any service, and a `CancellationToken`. `[AcceptsExtensions(typeof(Order))]` opens the tenant-field channel (Step 9). A `Result` from a shared rule narrows to `Result<Output>` with `.As<Output>()`; a bare `Output` converts implicitly.

The business rules live once, shared with derivations (Step 3):

```csharp
public static class OrderRules
{
    public static async Task<Result> CustomerCanReceiveOrder(
        CustomerId customerId, TenantId tenant, ErpDbContext db, CancellationToken ct)
    {
        // Customers are group-shared reference data (Step 15) — hence the explicit scope.
        var customer = await db.Customers.WithInherited(db, tenant)
            .Where(x => x.Id == customerId)
            .Select(x => new { x.IsActive })
            .SingleOrDefaultAsync(ct);
        return customer is { IsActive: true } ? Result.Success() : OrderErrors.InvalidCustomer;
    }
    // ProjectBelongsToCustomer(...) similar
}
```

**Derived from this file alone** — no further code:

| Artifact | Result |
| --- | --- |
| HTTP endpoint | `POST /api/operations/orders.create` |
| OpenAPI + JSON Schema | Input/Output schemas from the records; nullability = requiredness |
| TypeScript client | `client.ordersCreate(input): Promise<TypedOperationResponse<OrdersCreateOutput>>` — flat camelCase methods over operation ids, generated from the manifest |
| MCP tool | `orders_create` with the same schema (Step 8) |
| Pipeline | authorization, transaction, structural validation, audit entry, idempotency (the `X-Idempotency-Key` header), correlation, `TenantId` stamping |
| Permission catalogue | `orders.create` appears in the manifest's catalogue; roles validate against it at define time (Step 15) |

The wire envelope every caller gets back:

```json
{
  "output": { "orderId": "7c9e1c1a-4b2e-4f5e-9d3a-0b54c7e3a1f2", "number": "2026-01417" },
  "findings": [],
  "effects": [ { "type": "entity-created", "entity": "order", "id": "7c9e1c1a-4b2e-…" } ],
  "newVersion": 0,
  "auditReference": "8f4c2f0b6c7d4e21a3b90d2f4a5b6c7d"
}
```

Effects name entities by their wire key — the kebab-cased CLR name (`order`), the same key Step 9's field registry and Step 13's packaged fields address. And `auditReference` points into `audit.entries`, which is itself an ordinary queryable view with a shipped admin grid (`web.audit.list`) — the audit trail is read through the same machinery it audits.

---

## Step 3 — Derivations: reactive form behavior, written once *(BUILT)*

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

## Step 4 — Bindings: one per boundary that differs *(BUILT)*

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

## Step 5 — The list: a view and its grid *(BUILT)*

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

Search is authored logic — a hand-written `Where` — while declared capabilities are contract *and* implementation: `Filterable(Status)` makes the framework compose the SQL predicate and the grid render the filter control, with no further code (D7). A grid column naming a field the view doesn't produce fails model build with `VIEW001`; a sort request on an undeclared capability falls back to the declared default at runtime. *(Designed, not built: the `Tam.Testing` harness that executes every declared capability against real PostgreSQL in CI — today the wire suites against the running samples cover that ground.)* Tenant custom fields filter and sort the same mechanical way (`?ext.machineSerialNumber=…`) — necessarily, since a runtime-defined field can never appear in a compiled Query record.

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

## Step 6 — Editing: partial, conflict-safe *(BUILT)*

```csharp
// samples/erp/Features/Orders.cs

[Operation("orders.edit-details")]
[Authorize("orders.edit")]
[Widens("orders.edit-all")]
[AcceptsExtensions(typeof(Order))]
public static class EditOrderDetails
{
    public sealed record Input(
        OrderId OrderId,
        Change<OrderDescription?>? Description = null,
        Change<DateOnly?>? RequestedDate = null,
        Change<Address?>? WorkAddress = null,
        Change<decimal?>? EstimatedTotal = null);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderErrors.NotFound;

        // The write-side twin of the list's scope: base-atom holders edit only their own orders.
        var scope = context.CheckOwnershipUnless("orders.edit-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        if (order.Status != OrderStatus.Open) return OrderErrors.NotEditable;

        var merge = TamMerge.Apply(order, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();

        return new Output(order.Version + 1);
    }
}
```

Read the `Change` types precisely, because the convention carries meaning: `Change<OrderDescription?>?` is nullable **inside and out**. The outer `?` means *absent = untouched* — a partial edit names only what the user touched. The inner `?` lets a present change carry `value: null` — an explicit clear. Every clearable field follows this double-nullable convention; a non-clearable field would keep its inner type non-nullable. Loading is ordinary EF through the global tenant filter — `SingleOrDefaultAsync` plus a not-found finding, no special load API — and the handler states the *business* precondition (only open orders are editable). Everything mechanical — dirty detection, three-way merge, semantic equality per value type, conflict shaping, field-level audit — is `TamMerge` and the pipeline.

Two dispatchers edit order `2026-01415` concurrently. Anna changes the description; Björn changed the requested date a moment earlier. Anna's submission — change fields ride *flat* on the input, there is no `changes` wrapper:

```json
{
  "orderId": "0b54c7e3-9d3a-4f5e-8b2e-7c9e1c1a41f2",
  "description": { "original": "Repair pump", "value": "Replace pump" }
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

The edit form binding lives beside the create form in the composition root (`web.orders.edit`, Step 4's pattern, with the record key hidden); the record surface prefills it from the `orders.detail` view (Step 18).

---

## Step 7 — Completing: an intent, not an edit *(BUILT)*

```csharp
// samples/erp/Features/Orders.cs

[Operation("orders.complete")]
[Authorize("orders.complete")]
[Widens("orders.complete-all")]
public static class CompleteOrder
{
    public sealed record Input(OrderId OrderId);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderErrors.NotFound;

        var scope = context.CheckOwnershipUnless("orders.complete-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = order.Complete();
        if (result.IsError) return result.As<Output>();

        return new Result<Output> { Output = new Output(order.Version + 1) }
            .Effect(new EventPublished("order-completed",
                new { orderId = order.Id.Value, number = order.Number.Value }));
    }
}
```

No `Change<T>` here, deliberately: status is consequential state, so it moves only through this intent — `EDIT001` fires at build time if anyone exposes an enum through a generic change-set. And note there is no extension channel either: tenant fields never ride on intents.

The effect is not a typed event class — it is `EventPublished("order-completed", payload)`, an anonymous payload against a *declared* contract: the host's model carries `.PublishesEvent("order-completed", "orderId", "number")` (Step 0's builder), which is what subscribers bind against and `PLG009` verifies (Step 17.4). The event commits through the outbox if and only if the transaction commits; the inferred `entity-modified` effects drive the grid's live refresh (decision D5); the audit trail records it all — none of it written here.

---

## Step 8 — What the machine callers see *(BUILT)*

The same feature, no extra code:

**HTTP** — `POST /api/operations/orders.create`, `orders.edit-details`, `orders.complete`; `GET /api/views/orders.list?status=open&sort=number&dir=desc&page=2` — filter and sort parameters are the camelCase wire names, enum values camelCase (`status=open`), direction a separate `dir`. OpenAPI documents all of it at `/openapi.json`.

**TypeScript** —

```ts
const result = await client.ordersCreate({
  customerId, orderType: "service",
  workAddress: "Industrigatan 4, Västerås",
  description: "Byt packning på huvudpump",
});
// result.output?.number — TypedOperationResponse<OrdersCreateOutput>, typed end to end
```

The generated client (`apps/web/src/generated/tam.ts`) is flat camelCase methods over operation ids; an `{ idempotencyKey }` option becomes the `X-Idempotency-Key` header.

**MCP** — tool names replace the id's dots and dashes with underscores (`orders_create`, `views_orders_list`), and the preflight tool rides the FORM id — interaction state is a form concern — so an agent asked to "create a project order for Acme's pump replacement" does:

```
→ tool: web_orders_create_resolve   { "customerId": "…", "orderType": "project" }
← { "fields": {
      "projectId": { "visible": true, "enabled": true, "required": true,
        "options": [ { "value": "…", "label": "Pumprenovering 2026" },
                     { "value": "…", "label": "Serviceavtal årligt" } ],
        "findings": [] },
      "workAddress": { "visible": true, "enabled": true, "required": true,
        "suggestedValue": "Industrigatan 4, Västerås", "findings": [] } },
    "findings": [], "revision": 3 }

→ tool: orders_create   { …full input… }
← { "output": { "orderId": "…", "number": "2026-01418" }, … }   ← the Step-2 envelope, verbatim
```

Every field's resolved state carries `visible`/`enabled`/`required`/`suggestedValue`/`options`/`findings`; the suggested address is a single string because `Address` is a single-string value type. Had the agent picked the credit-blocked customer instead, a `customers.credit-blocked` warning finding would arrive with its `message` resolved in the connection's culture ("Kunden är kreditspärrad.") from the same catalogs the web form uses — the `code` is what the agent branches on. Idempotency is the same `X-Idempotency-Key` HTTP header on the MCP request, not a body property. The agent hit the same derivations, the same validation, the same audit trail as the web form — `resolve` is the form runtime's endpoint wearing a tool schema. There is no agent-specific business logic anywhere in the feature.

---

## Step 9 — The tenant adds a custom field. Nobody deploys anything. *(BUILT)*

A tenant administrator (or an agent on their behalf) calls a framework operation — the registry is itself just operations:

```json
POST /api/operations/extensions.define-field
{
  "entity": "order",
  "key": "machineSerialNumber",
  "type": "text",
  "maxLength": 40,
  "labels": { "sv": "Maskinserienummer", "en": "Machine serial number" },
  "descriptions": {
    "sv": "Serienummer för den servade maskinen, från typskylten.",
    "en": "Serial number of the serviced machine, from the type plate."
  }
}
```

The entity is the wire key from Step 2's effects (`order`); the payload is exactly the registry's surface — key, type, labels, `required?`, `maxLength?`, `options?` for selections. The registry runs its checks at definition time — unknown entity (`EXT007`), unknown type, malformed key, key collision (`EXT005`), missing default-culture label (`EXT006`, the registry twin of `L10N001`) — then activates the field and bumps the tenant's manifest revision. *(Designed, not built: declarative placement and per-field write permissions — today the field splices in wherever a binding put its `.Extensions()` marker, and writes ride the operation's own permission.)*

Because every binding in this tutorial opted in with `form.Extensions()` / `grid.Extensions()`, the field is **immediately**:

- an input on the web create and edit forms at the splice point, validated to 40 characters, rendered by the standard `text` renderer;
- a column and filter in the orders grid (`?ext.machineSerialNumber=…` — JSON-translated on the database, equality/range/contains operators derived from the declared type);
- carried in `orders.edit-details` submissions as `"extensions": { "machineSerialNumber": { "original": null, "value": "MX-55012" } }` — the same change-set shape, three-way merged with structured conflicts;
- in the audit trail with the operation that wrote it *(per-key audit granularity is designed, not built — today the audit entry records the extension column's change)*;
- in the MCP tool schema with the admin's description — agents read and write it like any field.

What it can never do: gate `orders.complete`, appear on an intent operation, or otherwise steer compiled business decisions — the pipeline holds that line (see [15-extensibility.md](15-extensibility.md)). *(Designed, not built: the graduation scaffold that promotes a load-bearing field to a compiled property with a data migration.)*

Custom fields have a sibling: **automation rules** — the tenant's declarative logic, bounded by
the same trust line. An admin defines "cold-chain orders need a requested date" as data: a Px
condition over the input (compiled and extension fields alike) and a blocking finding, validated
at definition time (`RUL001` unknown operation, `RUL002` unknown field, `RUL003` missing
default-culture message), evaluated without loops, code, or HTTP — and fully audited. The
executor has no rules special case: rules run as the `tam.rules` package's own wildcard gate,
through the very seam Step 16's approvals plugin uses; and because Px is portable, the same
condition drives client-side form behavior without a round trip. What rules never get is
arbitrary code or writes to compiled fields — EDIT001's philosophy, extended to tenants.

---

## Step 10 — The integration is a mapping, not a sync engine *(BUILT — `samples/fortnox`)*

In the running system the Fortnox integration is not host code at all — it is a plugin, activation- and entitlement-gated, and its whole job is one mapping onto the `orders.create` *wire* contract:

```csharp
// samples/fortnox/FortnoxPlugin.cs (trimmed)

[TamPlugin("fortnox")]
public sealed class FortnoxPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // The read footprint is a BUILD-TIME fact (docs/31 D-X3): the install screen shows exactly this.
        plugin.RequiresView("customers.lookup", "id");

        // POST /api/integrations/fortnox.orders.import — a JSON array of Fortnox orders.
        plugin.Integration(
            "orders.import", "orders.create",
            key: row => Str(row, "documentNumber"),   // the vendor id is the idempotency key
            map: MapOrderAsync);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> MapOrderAsync(
        JsonElement row, IServiceProvider services, OperationContext context, CancellationToken ct)
    {
        // Resolve the vendor's customer NAME to our id through the host's customers.lookup VIEW,
        // as the request's actor — the blessed read seam, never a host table or CLR type.
        var views = (IHostViewReader)services.GetService(typeof(IHostViewReader))!;
        var lookup = await views.RowsAsync(
            "customers.lookup",
            new Dictionary<string, string?> { ["search"] = Str(row, "customerName"), ["pageSize"] = "1" },
            context, ct);
        object? customerId = /* first row's "id", else null */;

        return new Dictionary<string, object?>
        {
            ["customerId"] = customerId,   // null → orders.create's rule fails the row (see below)
            ["orderType"] = "service",
            ["workAddress"] = Str(row, "deliveryAddress"),
            ["description"] = Str(row, "description"),
        };
    }
}
```

Imported orders execute `orders.create` — same authorization (as the request's actor), same rules, same findings, same audit. The inbox stores each source row before processing and re-maps it on every retry, so a row that failed because the customer didn't exist yet recovers — with no re-send from the partner — once the customer is created; rows that keep failing dead-letter after bounded retries. The plugin references no host CLR type: it maps to the wire contract and reads through a *declared* view footprint. That a whole external-integration capability is a removable, per-tenant-priced plugin — over the same seams as fields and gates — is the extensibility thesis at full stretch.

One scoping note: a host-authored integration can instead use the typed `IntegrationBuilder<TSource, TInput>`, where forgetting to map a required field fails model build with `INT001`. The plugin path above maps wire dictionaries — a missing field there surfaces as a validation finding on the row (retried from the inbox), never a 500 out of the endpoint.

Traffic flows the other way too, and it needs credentials. The tenant's Fortnox API key lives
in the **secrets vault** — `secrets.set` stores only Data-Protection ciphertext, and there is
deliberately no read-back operation: `secrets.list` returns keys and a set/unset flag, never a
value, not even masked; the plaintext exists transiently, in-process, only while a run executes.
Non-secret config (`fortnox.baseUrl`) is `settings.set`, readable in the clear. **Outbound
integrations** are declared like inbound ones and triggered three ways — by a committed effect,
on a schedule, or manually: `push-completed-order` fires on `order-completed`, reads the base
URL and key from the vault, POSTs the order to the accounting API, and records a run in the
per-tenant run history (with retry, backoff and dead-lettering from the same integrations
machinery). No `HttpClient` in domain code, no credential in any config file, and the SSRF
guard blocks private-network destinations unless the host explicitly opts in.

---

## Step 11 — Tests exercise the contract, not the plumbing *(the harness: DESIGNED, NOT BUILT)*

The design calls for an `OperationTest` base that runs the real pipeline — authorization, transaction, merge, audit — against a real database, with `Given.*` fixtures and assertions like `result.ShouldFailWith("orders.invalid-customer")` or `ShouldHaveEffect<EntityCreated>(e => e.Entity == "order")`, so that what's green in a feature's test file is what's true in production. **That harness does not exist yet.** Nothing ships it, and no sample uses it — treat any `OperationTest` snippet you find in older drafts as fiction.

What you write today is ordinary xUnit against the framework's real seams — this one is `tests/Tam.Tests/MergeTests.cs`, verbatim:

```csharp
[Fact]
public void Non_overlapping_stale_edit_merges_cleanly()
{
    var doc = new Doc();
    // Current Description already moved to "Replace pump" by user B:
    TamMerge.Apply(doc, new EditInput(Description: new("Repair pump", "Replace pump")));

    // User A, holding the old base, edits a DIFFERENT field:
    var merge = TamMerge.Apply(doc, new EditInput(
        RequestedDate: new(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 22))));

    Assert.False(merge.HasConflicts);
    Assert.Equal(["requestedDate"], merge.AppliedFields);
}
```

The framework's own suite covers the pipeline at this level — merge semantics, paired-atom scoping, tenant isolation, plugin seams, RLS — and the Step 16/17 scenarios run as wire suites against the running samples. Manifest pinning exists as the D4 baseline check (Step 0), not as per-feature snapshot tests.

---

## Step 12 — Six months later: change impact *(the unified report: DESIGNED, NOT BUILT)*

A developer adds `CustomerReference` to `CreateOrder.Input` as a required field. The design target is a single build-time answer:

```
Added CreateOrder.Input.CustomerReference (required)

✓ HTTP + OpenAPI schema updated
✓ Web create form: field added
✓ MCP tool schema updated
✓ TypeScript client regenerated
✗ INT001 fortnox.orders.import does not map required field CustomerReference
✗ MANIFEST: non-additive change vs. release baseline — requires baseline approval (D4)
```

**That consolidated report is not generated yet.** What exists today are its two red lines, separately: the D4 baseline check (`scripts/check_manifest.py`) fails CI on any non-additive manifest change — a new required input included — until the new baseline is consciously committed; and a *typed* integration that stops mapping a required field fails model build with `INT001` (the wire-mapped fortnox plugin instead fails per row, Step 10). The manifest's `gatedBy`/`subscribedBy` sections (Steps 13 and 17) carry the plugin-coupling half of the story. The green checkmarks above are true but silent — the derivation chain simply regenerates. What's missing is the tool that says all of this in one place.

---

## Step 13 — A partner ships a plugin *(implemented — [22-plugins.md](22-plugins.md), decision D8; running in samples/inspect)*

Norrservice's certification partner sells an inspection-checklist capability. It arrives as a NuGet package, and the host application adds two lines — one to the model and, because this plugin ships its own entities, one storage opt-in in the DbContext (Step 0's pattern):

```csharp
model.AddPlugin<InspectionPlugin>();               // Program.cs — the model
InspectionPlugin.AddInspect(modelBuilder);         // Db.cs — the plugin's tables, in the host database
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
        // orders.complete.gatedBy: ["inspect"] and in the impact report.
        plugin.Gate<ChecklistGate>("orders.complete");

        // A reaction to a committed HOST effect — post-commit, off the outbox, in a scope
        // pinned to the record's tenant. Completing an order opens its follow-up checklist.
        plugin.OnEffect<OpenFollowUpChecklist>("order-completed");
    }

    // Handlers are classes constructed per invocation with CONSTRUCTOR injection — the ctor
    // signature is the dependency declaration, exactly like an operation handler's parameters.
    // The gate reads the wire input and the plugin's OWN data, never host CLR types; the
    // ambient tenant filter scopes the query, so no hand-written TenantId predicate.
    private sealed class ChecklistGate(ITamDb tam) : IOperationGate
    {
        public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
        {
            if (!gate.Input.TryGetProperty("orderId", out var idElement)
                || !idElement.TryGetGuid(out var orderId))
                return Result.Success();   // malformed input is validation's problem, not the gate's
            var blocked = await tam.Db.Set<Checklist>().AnyAsync(
                x => x.OrderId == orderId && !x.Passed, ct);
            return blocked ? InspectFindings.ChecklistIncomplete : Result.Success();
        }
    }

    private sealed class OpenFollowUpChecklist(ITamDb tam) : IEffectHandler
    {
        public Task HandleAsync(EffectEvent effect, CancellationToken ct)
            => Task.CompletedTask; /* create the follow-up checklist, idempotently */
    }
}
```

Because the packaged field rides the extension channel, it is already in every grid, form, audit trail, MCP schema, and D7 filter — none of that is plugin code. Because the gate is declared, `orders.complete` in the manifest now reads "gated by inspect" — the fact Step 12's (designed) impact report will surface when anyone touches `CompleteOrder`.

The tenant admin flips it on — `plugins.activate("inspect")` — an audited framework operation like any other. For tenants that haven't, the manifest simply omits everything: no nav entry, no MCP tools, no packaged field, HTTP 404 on `inspect.*`. Installing code was the vendor's deploy; enabling it was the tenant's click. And the trust line holds: the partner wrote C# through a compiler and a review; the *tenant* still authors only data — fields, roles, packages, and (later) Px-bounded automation rules (built — see Step 9) and, later, custom objects, per D8.

Two more tiers ride the same machinery, in opposite trust directions. **The framework itself is
packages**: `AddTamSystem()` is a set of `[TamPackage]` modules — `tam.users`, `tam.roles`,
`tam.audit`, `tam.tenancy`, `tam.rules`, … — registering through the SAME `PluginBuilder`
surface the inspection vendor used, differing only on the tier axes: always active (never in
the activation table — who activates the activator), claiming their historical wire prefixes
(`users.invite`, `audit.entries`) instead of a namespace, and framework-trusted. Every admin
grid Norrservice clicks exercises the plugin seams daily — the strongest regression guard the
seams can have. And below plugins sit **tenant packages**: everything the registry accepts one
item at a time — fields, roles, rules — expressed as one JSON document and installed as one
act, with dry-run findings, atomic apply, version-guarded upgrade, and retire-on-uninstall
(data outlives configuration). A consultant carries the same `cold-chain` package to ten
customers; it lives in a repo and gets code review, because it is a file.

---

## Step 14 — Who is asking, and what have they paid for *(implemented — [24-subscriptions.md](24-subscriptions.md))*

Everything above assumed an actor with permissions. Two framework capabilities produce that actor and bound it, and neither is application code.

**Identity is the framework's own** (`Tam.Auth.OpenIddict`, behind the `IActorProvider` seam). The app calls `AddTamOpenIddict<ErpDbContext>()` and gets an embedded OpenIddict token server: humans sign in with Authorization Code + PKCE on a framework-rendered, localized login page (no password grant — OAuth 2.1), pick their organization when they have more than one, and the SPA renews a 10-minute access token silently with a rotating refresh token. Agents and integrations use client credentials (a machine client acts as a same-named account, so an agent has roles and an audited identity like any human). Accounts are platform-global with per-tenant memberships ([26](26-tenancy-hierarchy-and-identity.md)); grants resolve *fresh from the membership's roles and policies on every request*, so revoking a role beats the token's lifetime. The token machinery is hardened for a public client holding its own tokens: one-time-use rotation with reuse detection (a replayed refresh token revokes its whole family), revocation on sign-out, entry validation so revocation bites immediately, and a pruned token store. Swap in any external IdP by replacing the provider; the rest of the framework never knows.

```csharp
builder.Services.AddTam<ErpDbContext>(model);
builder.Services.AddTamOpenIddict<ErpDbContext>();   // the whole auth story, one line
app.MapTamAuth();                                    // /connect/authorize + /connect/token + login/picker/invite pages
```

**Entitlements bound what that actor can reach** (docs/24). A tenant's subscription — plan, seats, plugin entitlements — is data a billing provider drives through `subscriptions.set-plan` (a service-actor operation, not the tenant admin: a Stripe webhook maps to one call). Two mechanical gates, both already the right place: `plugins.activate` refuses a plugin the plan doesn't entitle (a localized upsell finding, not a crash), and `users.define` refuses a new user past the seat ceiling. A tenant tree with no anchor on its chain falls to the host-configured `unconfigured` default — bootstrap-sized, anchored at the root so child nodes never mint fresh seat pools — and self-hosted deployments set `SubscriptionDefaults.Unlimited` once, so the framework runs fully without any billing wired up. This is how the inspect and fortnox plugins of the last two steps become things a tenant *buys*: the marketplace adds the plugin id to `PluginEntitlements`, and activation starts succeeding — the framework never touches money, it reads one boolean.

When Norrservice becomes a group (next step), the subscription does what capability does:
**it cascades**. A subscription row is an *anchor* — it covers its own node and every
descendant until a nearer anchor shadows it — so the Kiruna company Norrservice acquires is
entitled to everything the group plan pays for the moment it is created, with no row of its
own. Seats pool at the anchor: the count spans the covered subtree and the seat lease lands on
the anchor's row, so two invites racing at different child companies conflict at commit instead
of both slipping under the ceiling — and one region admin with a cascading membership is one
seat, not fifty. A subsidiary on genuinely separate billing gets a **sub-anchor**
(`subscriptions.set-plan` run while acting at that node — billing-provider-only by
construction, since `subscriptions.manage` is a reserved atom no role can grant), and the
boundary is absolute: no entitlement unions, no seat borrowing. `tenants.move` across an anchor
boundary succeeds with `subscriptions.entitlement-lost` / `seat-overflow` WARNINGS — a re-org
is never held hostage to billing, and enforcement rides the ordinary downgrade semantics.

The whole request now reads top to bottom as data: *the OpenIddict token names a user → the user's roles resolve to grants → the grants pass the operation's `[Authorize]` → the plan entitles the plugin the operation belongs to → the seat/entitlement gates hold → the pipeline runs → the audit row records the human's name.* Every arrow is a row in a table a tenant admin or a billing webhook can change without a deploy.

## Step 15 — Norrservice becomes a group *(implemented — [26](26-tenancy-hierarchy-and-identity.md) + [27](27-authorization-model.md))*

A year in, Norrservice buys a company in Kiruna. Nothing about Orders changes — what changes is *who stands where*.

**The tree is data.** An admin opens the Companies page (or calls `tenants.create`) and adds `nord` under `demo`; the registry stores a materialized path (`demo.nord`), and every data row keeps carrying exactly one `TenantId` — nothing is denormalized, so re-parenting a company later (`tenants.move`) rewrites paths in the tiny tenants table and touches no data row. Renames are `tenants.rename`; the whole lifecycle is operations, so it is authorized, audited and localized like everything else.

**Grants fan out; writes fan in.** Alva's one membership at `demo` carries `admin` with `cascade: true`, so she can *stand at* any descendant — the login picker and the header switcher offer the whole standable set, labeled by path ("Demo AB ▸ Norrservice Nord AB"). Standing at `nord` (an `X-Tam-Tenant` act-as header the client sets), everything she does — creates, audit rows, events, idempotency — lands in `nord`, because the whole request is re-bound to the target node. Reads widen only where a view asks for it: the orders list itself declares `SubtreeRead` (the standard view IS the group roll-up — the dedicated overview it once had duplicated the real list and is retired; Step 18 shows the grid it drives), the customer lookup declares `inherited` (group-owned reference data readable from every leaf), and everything else stays strictly node-local. The compiler enforces the sharp edge here: composing a widened source into a query without explicitly scoping the other side is a build error (TAM005), because EF's filter opt-out is query-wide.

**Creates fan in to one explicit node — without switching.** A create targets a row that does
not exist yet, so its tenant must be *bound*, never inferred from a rolled-up view (D-H4).
Alva, holding a cascading `orders.create` at `demo`, gets a **target-node field** on the create
form — a lookup over the nodes where her cascaded capability grants create — so she books an
order into `nord` without leaving `demo`, the Azure resource-group pattern. A leaf worker whose
create capability covers only the active node never sees the chooser. The validated target
becomes the request's **execution tenant**, not just a column value: audit, outbox events,
idempotency and the form's own lookups (picking the sub-company's customer) all land in the
target with the row. And the same rebind runs per row on the group's grids: acting on a child
company's row acts *in* that company (Step 18).

**Access is capability — including row reach.** A role says what you can *do*, authored as access levels (`{"orders": "manage"}`) expanding to permission atoms at load time, with `[Sensitive]` fields maskable down to reads *and* writes. Row reach rides the same atoms as the **paired-atom pattern** ([28](28-assignment-and-grouping.md)): `orders.read` is own-scoped by default, `orders.read-all` widens — so the technician role carries the base atoms and sees only assigned orders on every surface, while the dispatcher role adds the `-all` atoms and works the whole board. No second registry, no policy admin: granting a role with or without the widening atom IS the runtime toggle, and TAM006 verifies at compile time that every view over a widened resource actually applies the scope — fail-closed by construction. The role registry is tenant data with an admin page, validates against the compiled catalogue at define time, and re-resolves per request.

**People arrive by invite.** `users.invite` creates the account and membership up front (the seat is consumed immediately, so the count the admin sees is the count that bills), mails a one-shot hashed link through the `ITamEmail` seam (the dev default logs it), and the invitee sets a password on a framework page. Inviting someone who already has an account elsewhere in the platform just adds the membership — one human, many tenants, one login.

The pattern of Steps 1–14 holds: the tree, the memberships, the roles, the policies, the invites are all *data behind operations* — no deploy moves a company, grants a scope, or seats a user.

**And under all of it, the database holds the line too** *(BUILT — [33-rls-backstop.md](33-rls-backstop.md))*. The EF global filter is the tenant boundary, but on PostgreSQL it is mirrored in the database itself: `TamRls.ProvisionAsync(db)` at startup creates a row-level-security policy over every `ITenantScoped` table, and `options.AddTamRls()` beside `UseNpgsql` keeps the session's `app.tenant_id`/`app.tenant_read_set` settings true to the ambient scope — including the subtree read set above. Sanctioned cross-tenant reads go through `AcrossTenants()`, THE opt-out that carries both halves (EF's filter skip and the database's query tag) together. The probe that motivated it: delete the app-side filter and the forgotten-filter query returns *zero* foreign rows, because the policy fails closed — one bug no longer leaks a tenant; it now takes two independent failures.

## Step 16 — Approvals arrive as a plugin — and the domains never notice *(BUILT — the seams and the package, `samples/approvals`)*

Norrservice's group buys an add-on from a workflow vendor: purchase approvals. Orders above a
threshold need a manager's sign-off; time corrections need the team lead. The point of this step
is what it does **not** require: no change to `CreateOrder`, no change to any domain, and no
approval engine in the framework. Groups and workflows are exactly the things docs/28 keeps out
of core — so they arrive the way inspection checklists did in Step 13: as a package the tenant
activates.

What the vendor ships — `samples/approvals`, ~400 lines all told: its OWN aggregates in the host
database like inspect's checklists (`ApprovalGroup` — nested, and nesting semantics are the
PLUGIN's problem, the framework never learns about groups — `ApprovalGroupMember`,
`ApprovalRule` over host *wire* operation ids with optional thresholds, `ApprovalRequest` — the
parked envelope keyed by its payload hash); `approvals.*` operations and views (rules and group
admin, request list, approve/reject) — each an ordinary operation: authorized, audited,
localized, in the manifest, an MCP tool; `OnEffect("approvals.requested")` mailing the effective
approver set through `ITamEmail`; and ONE wildcard gate.

The interesting part is that gate. Step 13's gate was declared against one known operation id;
approvals intercepts operation ids *the tenant configures at runtime*, parks the request, and
later runs it for real. Walking through one order, exactly as the wire verification replays it:

1. Didrik submits `orders.create` for 180 000 kr. The approvals gate (running inside the
   pipeline, before the handler's effects commit) consults its `ApprovalRule` table: this
   operation + this threshold ⇒ sign-off required. The gate **parks the envelope** — operation
   id, wire body, payload hash, actor, culture — as an `ApprovalRequest` via
   `gate.Park<ParkEnvelope, ApprovalRequest>(…)` (the domain transaction rolls back; the
   envelope commits from a fresh scope; an identical resubmit re-blocks but dedupes on the
   hash), and blocks with `approvals.pending` (a localized finding the form
   renders as "submitted for approval", not as an error).
2. The rule's group resolves — members of the group plus every nested subgroup —
   `OnEffect` mails the approvers, and the pending request sits in the plugin's grid.
3. A lead runs `approvals.approve` (four-eyes: never the initiator; membership checked through
   the nesting). On commit, the plugin **replays the parked envelope** through the real
   pipeline — the same executor every caller uses — as the *original* actor with grants
   re-resolved as of now, marked `InvocationSource.Workflow` (the gate's pass condition, settable
   only by compiled code). The order is created; the audit trail shows both facts: the
   `orders.create` entry reads actor Didrik / source Workflow / correlation = the request id,
   and the `approvals.approve` entry names the lead who released it.

The domain wrote none of this. `CreateOrder` still doesn't know approvals exist — for tenants
without the plugin, nothing changed; for tenants with it, the manifest says `orders.create` is
gated and the impact report shows it, exactly like Step 13.

**What this scenario proves — and the three seams it demanded.** This is the sharpest stress
test of the plugin architecture so far. The three gaps it exposed are now framework seams, each
proven end to end through the real pipeline in the test suite:

1. **Config-driven gate targets — built.** A gate registered as `plugin.GateAll<MyGate>()`
   runs on EVERY operation (after the operation-specific gates) and receives `gate.OperationId`,
   so which operations it actually blocks is a lookup in the plugin's own `ApprovalRule` rows —
   tenant data, not compile time. Every other gate contract is unchanged: wire input only,
   activation-gated, inside the transaction.
2. **Parking survives the rollback — built.** A blocking gate calls
   `gate.Park<MyParkedWork, TState>(state)`; the pipeline rolls the domain transaction back
   FIRST, then CONSTRUCTS the parked-work class in a fresh service scope pinned to the same
   tenant — so its injected `ITamDb` is a fresh context *by construction*, and the rolled-back
   gate scope is structurally unreachable (state crosses only as the explicit value). Work
   parked by a gate that ends up *allowing* the operation is discarded, so nothing leaks from
   attempts that went through. The `gate.PayloadHash` (the pipeline's idempotency hash) is the
   natural envelope key.
3. **Sanctioned envelope replay — built.** `EnvelopeReplay.ReplayAsync` re-executes a stored
   envelope through the FULL pipeline — authorization, validation, rules, gates, audit — as the
   ORIGINAL initiator, whose grants are re-resolved *as of now* (a revoked role or deactivated
   account fails the replay closed; approval releases a block, it never escalates). The run is
   marked `InvocationSource.Workflow` — the parking gate lets it pass while every other gate
   still runs — and the envelope id rides `CorrelationId` into the audit entry and doubles as
   the initiator-scoped idempotency key, so a redelivered approval replays the stored outcome
   instead of executing twice. Dual attribution falls out: the operation's audit names the
   initiator and correlates to the envelope, whose own trail names the releaser.

With the seams in place, "an opinionated nested-group approval system for existing domains,
without the domains knowing" stopped being a slogan: `samples/approvals` is that package, and
the whole scenario above — activate, configure, block, park, notify, approve through a nested
group, replay, reject, dedupe — is verified on the wire against the running sample. `CreateOrder`
was not touched. That is the bar docs/22 set for the plugin architecture, met.


---

## Step 17 — Invoicing arrives as a plugin — and Orders still doesn't know *(BUILT — [31-cross-domain-plugins.md](31-cross-domain-plugins.md), `samples/invoicing`)*

Step 13's plugin lived beside the domain; Step 16's gated it. Step 17 is the vendor plugin that
becomes part of another domain's DAILY SURFACE — invoicing inside Orders — and the bar does not
move: no host CLR type anywhere in the plugin, no host edit beyond the one-line storage opt-in,
everything per-tenant activated and entitlement-priced.

1. **The vendor's aggregate.** `Invoice` carries a wire-key `Guid OrderId` (no navigation, no
   FK) and a DENORMALIZED `OrderNumber` — cross-boundary joins don't exist (D-X3), so the
   number is copied at the moment it is known. Host opt-in: `AddInvoicing(modelBuilder)`.
2. **Its own vertical.** `invoicing.create-from-order` / `finalize` / `mark-paid`, an invoice
   list whose Query takes `Guid? OrderId` (the mechanical filter), embedded sv/en locales, a
   nav contribution. Nothing new — Step 13 machinery.
3. **Compatibility, stated at build time.** `plugin.RequiresView("orders.detail", "id",
   "number", "estimatedTotal")` — PLG008 fails the BUILD if the host doesn't expose exactly
   that, and the create operation reads the order through the ACTOR-mode `IHostViewReader`:
   permission-checked like the wire, so a user who may not see an order cannot invoice it.
4. **The draft writes itself — against a contract, not folklore.** An
   `OnEffect("order-completed")` subscriber drafts the invoice post-commit — idempotent under
   redelivery, number from the payload, amount backfilled through a SERVICE-MODE declared read
   (no actor exists in the outbox; the readable surface is exactly the RequiresView list,
   never a superuser). The payload shape is a build-time fact (D-X5): the host declares
   `.PublishesEvent("order-completed", "orderId", "number")` in its model, the plugin declares
   `plugin.RequiresEvent("order-completed", "orderId", "number")`, and PLG009 fails the BUILD
   on a subscription to an unknown event or a required field the publisher doesn't carry. The
   manifest gains an `events` section — `"order-completed": { "fields": ["orderId","number"],
   "subscribedBy": ["inspect","invoicing"] }` — so `SubscribedBy` shows in the impact report,
   symmetric with `GatedBy`.
5. **Invoicing pushes back.** A gate on `orders.complete`: an order with a PENDING DRAFT
   invoice cannot complete. The gate reads the plugin's own table off the wire input; the
   manifest shows `orders.complete → gatedBy: ["invoicing"]`.
6. **The order wears its invoice status.** `plugin.ExtensionField("order", "invoiceStatus",
   "selection", options: draft|invoiced|paid, readOnly: true)` — and the missing write half,
   D-X2's `IPackagedFieldWriter`: structurally scoped (only this plugin's declared keys),
   semantically validated, audited with the PLUGIN as the attributed actor, live-refreshing
   open grids. `readOnly` marks plugin-owned state: the field renders in grids and filters
   like any extension field but is EXCLUDED from forms, and the wire extension channel rejects
   writes (`extensions.read-only-field`) — the status shows everywhere, and its state machine
   stays the plugin's. Tenant-defined fields are never read-only. The column and filter appear
   on the host's orders grid with zero host changes.
7. **"Create invoice" where the user lives.** D-X1: `plugin.GridAction("web.orders.list",
   "invoicing.create-from-order", bind => bind.Field("orderId", fromColumn: "id"))` — a row
   action ON THE HOST'S GRID, declared bind instead of name-convention, PLG006-verified,
   rendered only for entitled+activated tenants and permitted users. It composes with the
   subtree grid for free: the button on a child company's row acts in that company.
8. **The tenant clicks Activate.** Entitlement-gated `plugins.activate`; before it, the
   operation, the field, the button and the nav page do not exist for that tenant — verified
   both ways on the wire, including a sibling tenant that never activates.
9. **What we didn't need.** Host code names the plugin nowhere; the capability manifest is
   DERIVED from the model: *reads your orders (id, number, estimatedTotal); adds field
   order.invoiceStatus; gates orders.complete; adds an action to your orders list; subscribes
   to order-completed.* The record-bound detail panel is one host line —
   `model.Slot("web.orders.detail", slot => slot.Key("orderId"))` plus one `.Slot(…)` section on
   the declared orders page (Step 18 — there is no modal React to edit) — and the invoice grid
   lands in it unnamed (D-X4); the accounting-provider push is Step 10's outbound seam applied,
   not new machinery.

The whole scenario — activate, create from the grid, duplicate rejected, ghost order rejected
by the declared read, status on the order row, plugin-attributed audit, complete blocked by the
draft, finalize, complete passes, auto-draft on a bare completion, paid rippling back onto the
order — runs as a 26-check wire suite against `samples/invoicing`. `CompleteOrder` and the
orders grid declaration were not touched.

---

## Step 18 — The composed UI: nav, pages, slots, and the subtree grid *(BUILT — [30-navigation.md](30-navigation.md), [32-pages.md](32-pages.md), docs/26 D-H1)*

Seventeen steps in, the model knows everything — operations, views, forms, grids, fields,
gates, actions, panels — and yet one surface was still hand-wired: the shell. Norrservice's app
carried a flat hand-written nav list and a ~50-line `OrdersPage` React component whose every
line was derivable (render the grid, on row click fetch the detail, open a modal with the edit
form, host the plugin slot). Both are gone. This step is where the whole UI becomes
manifest-composed — and, as always, the point is what we do NOT write.

**Nav is a declared tree.** The host owns layout; here is Norrservice's, in the model builder:

```csharp
.Nav("web", nav => nav
    .Mode("work", m => m
        .Page("orders", page: "orders", order: 10)       // DECLARED pages (below)
        .Page("customers", page: "customers", order: 30))
    .Mode("admin", m => m
        .Section("administration")))
```

Two rules make this scale past one app. **Contribution is not placement**: the invoicing plugin
declared `plugin.Nav(nav => nav.Page("invoicing.invoices", grid: "invoicing.web.invoices",
suggest: "work", order: 40))` — a *suggestion* of a semantic section, never a position in a
layout it cannot see; the framework's own admin packages suggest `administration` the same way,
and the host's `Section("administration")` collects them. Anything nobody places lands on an
auto-page under a well-known "more" section in the last mode — nothing can be authored into
invisibility; declaring nav is how a plugin graduates from "appears" to "belongs". And
**visibility is derived, never authored**: a page shows iff the actor holds the bound view's
permission, a section or mode shows iff any descendant does — so a role-oriented workspace
falls out free (a technician whose permissions empty the `admin` mode never sees the switcher
entry), and there is no `visibleWhen` to audit. Nav is discoverability, never authorization:
hiding removes the menu entry, not the surface. The manifest gains a `nav` section per surface
class (`"nav": { "web": [ { "id": "work", "kind": "mode", "labelKey": "nav.work",
"children": [ … ] } ] }`), labels are locale keys like everything else, node ids are D4-permanent,
and a tenant overlay closes the loop: the `tam.nav` package ships `nav.override` (hide,
per-culture relabel, reorder, move a page into another section — a CLOSED mutation set) and
`nav.retire` (restore the default) as ordinary audited operations over registry rows, overlaid
onto the tree at the manifest route. It is the custom-fields pattern applied to navigation —
tenant customization as data, never code — and an override whose node vanished (say the plugin
was deactivated) is dormant, not broken: reactivate and the tenant's placement returns intact.

**The orders page is a declared composition.** The React component this replaces is deleted:

```csharp
.Slot("web.orders.detail", slot => slot.Key("orderId"))     // the contribution point (Step 17)

.Page("orders", page => page
    .Grid("web.orders.list")                        // sections render in DECLARATION ORDER
    .Record(record => record
        .Detail("orders.detail", key: "orderId")    // fetched with the clicked row's id
        .Title("number")                            // detail field shown in the record title
        .Form("web.orders.edit")                    // prefilled from same-named detail fields
        .Slot("web.orders.detail")))                // invoicing's panel lands here, unnamed
```

A page is an ordered list of sections; the first grid opens the record surface, itself an
ordered list of form and slot sections — declaration order IS layout order, so there is no
`after:` annotation to disambiguate. Form prefill is the row-action convention made
declarative: each form field takes the same-named detail field, the record's key takes the
clicked row's id. `PAGE001` verifies every part exists and fits at build time; `SLOT001` fails
the build on a declared slot no page renders (a slot nobody renders is plugins contributing
panels into a void — the nav "more" lesson, applied to slots; a slot the app hosts in custom
React declares `external: true` and nothing else). Pages are the host's, like layout: plugins
reach them through slots and grid-action contributions, never by declaring pages. And the nav
entry above needed no permission atom — a `{ page }` target naming a declared page derives its
visibility from the page's grid's view, exactly like a `{ grid }` target. `registerPage(key,
component)` remains the escape hatch for genuinely custom UX, and its ratio is the architecture
tripwire: if most pages need React, the model is decorating the app. Norrservice is at zero.
Customers got the same treatment — a second `.Page("customers", …)` declaration in Program.cs,
grid + record + edit form, no slot — proving the shape generalizes without ceremony.

**The subtree grid.** Step 15 made Norrservice a group; here is what the group *sees*. The
orders list — the same view, not a twin — declares one capability:

```csharp
public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
    .Sortable(nameof(Result.Number), nameof(Result.CustomerName), nameof(Result.RequestedDate))
    .Filterable(nameof(Result.Status), nameof(Result.Type))
    .SubtreeRead(nameof(Result.TenantId))           // docs/26 D-H1: THE list is also the roll-up
    .DefaultSort(nameof(Result.Number), descending: true);
```

Standing at a leaf, nothing changes. Standing at `demo`, the ambient READ scope widens to the
acting node's validated subtree for that execution — the authored query is untouched — and the
named result field becomes three mechanical client behaviors at once: a **company column**
(labeled by tenant display name, rendered only when there is more than one company to tell
apart), a **tenant filter**, and **per-row act-as** — clicking a `nord` row fetches the detail,
submits the edit form, and fires row actions with `X-Tam-Tenant: demo.nord`, so every write
still fans in to exactly one node (writes never widen; the stamp reads only the current node).
Step 17's contributed "Create invoice" button composes for free: on a child company's row it
acts in that company. The manifest carries one fact — `"subtree": "tenantId"` on the view —
and the one sharp edge stays compile-checked: a query composing a widened source must scope its
other sides explicitly (`InScope` beside `WithInherited`), or TAM005 fails the build.

**The frontend, in its entirety.** The app shell composes slot components and owns nothing else:

```tsx
<NavProvider>
  <NavModeSwitcher />   {/* depth 0 — modes */}
  <NavSidebar />        {/* depth 1 */}
  <NavTabs />           {/* depth 2 */}
  <NavPage />           {/* declared pages via <ModelPage>, grids via <ViewGrid>, registered pages by key */}
</NavProvider>
```

**What we did NOT write:** routes, menu arrays, page components, modal wiring, a
detail-fetch-then-prefill effect, a company column, a tenant filter, an act-as plumbing layer,
or any per-plugin nav code. The sample's last hand-written page is deleted, and every plugin
from Steps 13–17 landed in this shell without the shell knowing their names.

---

## The tally

**Written by hand** (the only independently maintained facts):

| File | Approx. lines | Decisions it owns |
| --- | --- | --- |
| `Domain.cs` (the orders slice) | ~80 | invariants, value types, status transitions |
| `Features/Orders.cs` | ~290 | operations, shared rules, derivations, views — every business decision |
| `Program.cs` (composition root: model half) | ~110 | forms, grids, nav, pages, slots, event contracts |
| `Db.cs` | ~60 | EF mapping, plugin storage opt-ins, the tenant boundary |
| `samples/fortnox` (the plugin) | ~120 | external mapping, idempotency, outbound pushes |
| `locales/sv.json` + `locales/en.json` | ~100 | every word a human reads, per culture |

**Derived, and therefore never drifting:** four HTTP endpoints and their OpenAPI, JSON Schemas, a typed TypeScript client, create/edit forms with reactive behavior, a grid with paging/sorting/filtering/search and gated actions, three-way merge and structured conflicts, audit, idempotency, outbox events, the MCP tools plus a resolve surface, integration inbox/retry/replay, and per-tenant custom-field participation across every one of those boundaries.

That ratio — roughly 750 handwritten lines owning every real decision, and everything mechanical derived — is the success criterion of [18-success-criteria.md](18-success-criteria.md) made concrete. The implementation runs this document top to bottom today: Steps 0–10 and 13–18 are the running samples; where a step is still design (the test harness of Step 11, the unified report of Step 12), it says so on the step.
