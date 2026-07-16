# Step 1 — Domain state *(BUILT)*

Plain C#. Semantic value types carry intrinsic meaning once; everything downstream reuses it. Note what is *absent*: no display text anywhere — labels and messages resolve by key from the locale files ([21-localization.md](../21-localization.md)). *(Designed, not built: the `L10N000` analyzer rule that makes a hardcoded display string a build error. What the build enforces today is the reverse direction — `L10N001`, below.)*

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
