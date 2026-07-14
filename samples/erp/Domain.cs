using Tam;

namespace Erp;

// ---- Semantic value types (labels live in locales/, never here — docs/21) ----

public readonly record struct CustomerId(Guid Value);
public readonly record struct ProjectId(Guid Value);
public readonly record struct OrderId(Guid Value);

public readonly record struct CustomerName(string Value);
public readonly record struct OrderNumber(string Value);

[Multiline, MaxLength(1000)]
public readonly record struct OrderDescription(string Value);

[Format("email")]
public readonly record struct EmailAddress(string Value);

[Format("phone")]
public readonly record struct PhoneNumber(string Value);

public readonly record struct Address(string Value);

public enum OrderType { Service, Project }

public enum OrderStatus { Open, Completed, Cancelled }

// ---- Finding factories: stable codes; text in locale catalogs ----

public static class OrderErrors
{
    public static readonly FindingFactory AlreadyCompleted = Finding.Error("orders.already-completed");
    public static readonly FindingFactory CannotCompleteCancelled = Finding.Error("orders.cannot-complete-cancelled");
    public static readonly FindingFactory InvalidCustomer = Finding.Error("orders.invalid-customer");
    public static readonly FindingFactory ProjectRequired = Finding.Error("orders.project-required");
    public static readonly FindingFactory ProjectNotForCustomer = Finding.Error("orders.project-not-for-customer");
    public static readonly FindingFactory NotFound = Finding.Error("orders.not-found");
    public static readonly FindingFactory NotEditable = Finding.Error("orders.not-editable");
}

public static class CustomerFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("customers.not-found");
    public static readonly FindingFactory Inactive = Finding.Error("customers.inactive");
    public static readonly FindingFactory CreditBlocked = Finding.Warning("customers.credit-blocked");
}

// ---- Entities: plain C#, invariants in methods, no framework base classes ----

public sealed class Customer
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

    public void Deactivate() => IsActive = false;
}

public sealed class Project
{
    private Project() { }

    public ProjectId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public CustomerId CustomerId { get; private set; }
    public string Name { get; private set; } = "";
    public bool IsOpen { get; private set; }

    public static Project Create(string tenantId, CustomerId customerId, string name) => new()
    {
        Id = new ProjectId(Guid.NewGuid()),
        TenantId = tenantId,
        CustomerId = customerId,
        Name = name,
        IsOpen = true,
    };
}

public sealed class Order : IExtensible, Tam.EntityFrameworkCore.IVersioned
{
    private Order() { }

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
    public long Version { get; set; }
    public ExtensionData Extensions { get; set; } = new();

    public static Order Create(
        string tenantId, OrderNumber number, CustomerId customerId, OrderType type,
        ProjectId? projectId, Address workAddress, OrderDescription description,
        DateOnly? requestedDate, decimal? estimatedTotal) => new()
    {
        Id = new OrderId(Guid.NewGuid()),
        TenantId = tenantId,
        Number = number,
        CustomerId = customerId,
        Type = type,
        ProjectId = projectId,
        WorkAddress = workAddress,
        Description = description,
        RequestedDate = requestedDate,
        EstimatedTotal = estimatedTotal,
        Status = OrderStatus.Open,
    };

    public void AssignTo(string actorId) => AssignedToActorId = actorId;

    public Result Complete()
    {
        if (Status == OrderStatus.Completed) return OrderErrors.AlreadyCompleted;
        if (Status == OrderStatus.Cancelled) return OrderErrors.CannotCompleteCancelled;
        Status = OrderStatus.Completed;
        return Result.Success();
    }
}
