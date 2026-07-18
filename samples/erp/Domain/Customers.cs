using Tam;

namespace Erp;

// The Customers aggregate: value types, finding factories, entity (docs/02).


// ---- Semantic value types (labels live in locales/, never here — docs/21) ----

// Reference types carry their PICKER (docs/34 M5 — the type carries the defaults): declare
// the lookup once, and every form field of this type renders a searchable select over the
// view. [LabelKey] rides the same channel where the convention key would mislead.
[LabelKey("labels.customer"), Lookup("customers.lookup")]
public readonly record struct CustomerId(Guid Value);


public readonly record struct CustomerName(string Value);


public static class CustomerFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("customers.not-found");
    public static readonly FindingFactory Inactive = Finding.Error("customers.inactive");
    public static readonly FindingFactory AlreadyInactive = Finding.Error("customers.already-inactive");
    public static readonly FindingFactory CreditBlocked = Finding.Warning("customers.credit-blocked");
}


// ---- Entities: plain C#, invariants in methods, no framework base classes ----

public sealed class Customer : Tam.EntityFrameworkCore.ITenantScoped
{
    private Customer() { }

    public CustomerId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public CustomerName Name { get; private set; }
    public EmailAddress? Email { get; private set; }
    public PhoneNumber? Phone { get; private set; }
    public Address VisitAddress { get; private set; }
    public bool IsActive { get; private set; }
    public bool CreditBlocked { get; private set; }

    public static Customer Create(
        string tenantId, CustomerName name, Address visitAddress,
        EmailAddress? email, PhoneNumber? phone, bool creditBlocked = false) => new()
    {
        Id = new CustomerId(Guid.NewGuid()),
        TenantId = tenantId,
        Name = name,
        VisitAddress = visitAddress,
        Email = email,
        Phone = phone,
        IsActive = true,
        CreditBlocked = creditBlocked,
    };

    // Deactivation is a retirement, not a deletion — orders keep referencing the customer.
    public Result Deactivate()
    {
        if (!IsActive) return CustomerFindings.AlreadyInactive;
        IsActive = false;
        return Result.Success();
    }
}
