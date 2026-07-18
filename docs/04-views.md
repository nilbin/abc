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

**Async when the query needs a prior await.** `Execute` may also return
`Task<IQueryable<Result>>` — the executor awaits it and then composes paging, sorting, and
filtering over the resulting queryable exactly as in the synchronous form. Use it when the
view must resolve something *before* it can shape the query (the `tam.documents` views await
the caller's visible-folder set first — [36-documents.md](36-documents.md)); the final
projection stays a translatable `IQueryable`, so capabilities and mechanical filters are
unaffected.

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

**A declared filter IS the filter** (decision D7 in [19-decisions.md](19-decisions.md)): the framework composes typed predicates over the view's result projection mechanically — no per-view `Where` code, no Query-record member per filter. The Query record carries only *authored* query logic the framework cannot derive (free-text search, cross-entity predicates). This is also what makes tenant custom fields filterable: a runtime-defined field can never appear in a compiled Query record, but it can always be filtered mechanically (`?ext.machineSerialNumber=…`).

One `Filterable(field)` declaration yields every operator the field's type supports, on the wire and in the grid — the client derives the control set from the same wire kind the server derives the operators from:

| Field type | Operators | Wire form |
| --- | --- | --- |
| enum, boolean, id | equality | `status=open` |
| date, number | equality + inclusive range | `requestedDate.from=2026-02-01&requestedDate.to=2026-02-28` |
| string (incl. semantic wrappers) | equality + substring + ordinal range | `customerName.contains=Nord`, `number.from=2026-01400` |
| extension fields (tenant/plugin/package) | same, typed by the declared spec | `ext.weightKg.from=100`, `ext.machineSerialNumber.contains=2201` |

Extension-field predicates are real JSON extraction, not string containment: per-provider translations (`json_extract` on SQLite, `jsonb_extract_path_text` + numeric cast on PostgreSQL) behind two owned DbFunctions, with the extraction key always coming from the declared overlay — user input only ever becomes the comparison value. Boolean extension filtering and extension *sorting* remain open (provider-divergent JSON boolean forms; index promotion is the performance path, docs/15).

Range bounds use the lifted comparison — a row whose cell is null is outside every range. Operator values are parsed into expression *constants*, never expression *structure*: the filter language can grow (via the portable Px AST) but a string-parsed expression DSL is banned (see D7's consequences).

## Extension fields in views

Views over tenant-extensible entities may opt in to carrying extension data (`view.Extensions(...)`), which makes active tenant-defined fields available to grid/report/export bindings without any per-field code. See [15-extensibility.md](15-extensibility.md).
