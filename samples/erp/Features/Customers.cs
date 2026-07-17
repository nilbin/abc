using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

[Operation("customers.create")]
[Authorize("customers.create")]
public static class CreateCustomer
{
    // Contact details are the sample's SENSITIVE fields (docs/27 D-A3): visible/writable only to
    // actors holding customers.sensitive (Manage level or "*"); masked for everyone else.
    public sealed record Input(
        CustomerName Name,
        Address VisitAddress,
        [property: Sensitive("customers.sensitive")] EmailAddress? Email = null,
        [property: Sensitive("customers.sensitive")] PhoneNumber? Phone = null);

    public sealed record Output(CustomerId CustomerId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var exists = await db.Customers.AnyAsync(x => x.Name == input.Name, ct);
        if (exists)
            return Finding.Error("customers.duplicate-name").At(nameof(Input.Name));

        var customer = Customer.Create(
            context.TenantId.Value, input.Name, input.VisitAddress, input.Email, input.Phone);
        db.Customers.Add(customer);
        return new Output(customer.Id);
    }
}

/// <summary>Conflict-safe partial edit of the shared registry entry — the Change&lt;T&gt;
/// pattern (docs/6), same as orders.edit-details. Contact details stay sensitive-gated.</summary>
[Operation("customers.edit-contact")]
[Authorize("customers.edit")]
public static class EditCustomerContact
{
    public sealed record Input(
        CustomerId CustomerId,
        Change<CustomerName>? Name = null,
        Change<Address?>? VisitAddress = null,
        [property: Sensitive("customers.sensitive")] Change<EmailAddress?>? Email = null,
        [property: Sensitive("customers.sensitive")] Change<PhoneNumber?>? Phone = null);

    public sealed record Output(CustomerId CustomerId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        // STRICT ambient filter — deliberately narrower than the list's inherited scope: an
        // ancestor's shared customer is visible below, but edited only at the node that owns
        // it (this read returns NotFound anywhere else).
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == input.CustomerId, ct);
        if (customer is null) return CustomerFindings.NotFound.Create();

        var merge = TamMerge.Apply(customer, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();

        return new Output(customer.Id);
    }
}

[View("customers.list")]
[Authorize("customers.read")]
public static class CustomerList
{
    // IsActive filtering is mechanical (declared Filterable); only Search is authored logic.
    public sealed record Query(string? Search = null);

    // Init-property record: EF composes sort/paging over member-init projections (see STATUS.md).
    public sealed record Result
    {
        public CustomerId Id { get; init; }
        public CustomerName Name { get; init; }
        [Sensitive("customers.sensitive")]
        public EmailAddress? Email { get; init; }
        [Sensitive("customers.sensitive")]
        public PhoneNumber? Phone { get; init; }
        public Address VisitAddress { get; init; }
        public bool IsActive { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        // Customers are the group's shared registry (docs/27 INHERITED scope): a node sees its own
        // customers plus its ancestors' — a group-level customer serves every company below it.
        // Upward only; a sibling subtree is never exposed.
        var customers = db.Customers.WithInherited(db, context.TenantId);
        if (!string.IsNullOrWhiteSpace(query.Search))
            customers = customers.Where(x => ((string)(object)x.Name).Contains(query.Search!));

        return customers.Select(x => new Result
        {
            Id = x.Id, Name = x.Name, Email = x.Email, Phone = x.Phone,
            VisitAddress = x.VisitAddress, IsActive = x.IsActive,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Name), nameof(Result.IsActive))
        .Filterable(nameof(Result.IsActive))
        .DefaultSort(nameof(Result.Name));
}

/// <summary>The record surface behind the declared customers page (docs/32): one row, fields
/// named to prefill the edit form. Inherited scope like the list — an ancestor's shared
/// customer opens read-only in spirit (edits land at the owning node).</summary>
[View("customers.detail")]
[Authorize("customers.read")]
public static class CustomerDetail
{
    public sealed record Query(CustomerId CustomerId);

    public sealed record Result
    {
        public CustomerId Id { get; init; }
        public CustomerName Name { get; init; }
        [Sensitive("customers.sensitive")]
        public EmailAddress? Email { get; init; }
        [Sensitive("customers.sensitive")]
        public PhoneNumber? Phone { get; init; }
        public Address VisitAddress { get; init; }
        public bool IsActive { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context) =>
        db.Customers.WithInherited(db, context.TenantId)
            .Where(x => x.Id == query.CustomerId)
            .Select(x => new Result
            {
                Id = x.Id, Name = x.Name, Email = x.Email, Phone = x.Phone,
                VisitAddress = x.VisitAddress, IsActive = x.IsActive,
            });
}

/// <summary>Lookup view backing customer pickers and agent option resolution.</summary>
[View("customers.lookup")]
[Authorize("customers.read")]
public static class CustomerLookup
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public CustomerId Id { get; init; }
        public CustomerName Name { get; init; }
        public bool IsActive { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        // Same inherited scope as customers.list: order pickers offer ancestor-owned customers too.
        var customers = db.Customers.WithInherited(db, context.TenantId).Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(query.Search))
            customers = customers.Where(x => ((string)(object)x.Name).Contains(query.Search!));
        return customers.Select(x => new Result { Id = x.Id, Name = x.Name, IsActive = x.IsActive });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name));
}
