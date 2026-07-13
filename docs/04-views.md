# 04 — Views

A view is a **typed read model**.

A view may represent:

- Detail data
- A list or grid
- A lookup
- A report
- Context for an operation
- Integration export data
- Agent-readable resources

## Example

```csharp
[View("customers.summary")]
public static partial class CustomerSummary
{
    public sealed record Query(CustomerId CustomerId);

    public sealed record Result(
        CustomerId Id,
        CustomerName Name,
        PhoneNumber? Phone,
        Address VisitAddress,
        bool IsActive);

    public static IQueryable<Result> Execute(
        Query query,
        AppDbContext db)
    {
        return db.Customers
            .Where(x => x.Id == query.CustomerId)
            .Select(x => new Result(
                x.Id,
                x.Name,
                x.Phone,
                x.VisitAddress,
                x.IsActive));
    }
}
```

## Grids are bindings, not backend concepts

A grid is not a separate backend concept. It is a **binding over a collection view** ([06-bindings.md](06-bindings.md)).

## Multi-entity forms are compositions

A form spanning several entities is not a generic multi-entity form. It is composed from:

```
Operation input
+ contextual views
+ possibly additional operations
```

## Capability declarations

Views declare which result fields support sorting, filtering, and searching, so bindings cannot promise capabilities the underlying query cannot deliver (see `VIEW001` in [12-compiler-and-manifest.md](12-compiler-and-manifest.md), and the refinement note on runtime translatability in [review-notes.md](review-notes.md)).

## Extension fields in views

Views over tenant-extensible entities may opt in to carrying extension data (`view.Extensions(...)`), which makes active tenant-defined fields available to grid/report/export bindings without any per-field code. See [15-extensibility.md](15-extensibility.md).
