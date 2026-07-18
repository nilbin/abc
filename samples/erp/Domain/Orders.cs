using Tam;

namespace Erp;

// The Orders aggregate: value types, finding factories, entity (docs/02).


public readonly record struct OrderId(Guid Value);

public readonly record struct OrderNumber(string Value);


[Multiline, MaxLength(1000)]
public readonly record struct OrderDescription(string Value);


public enum OrderType { Service, Project }


public enum OrderStatus { Open, Completed, Cancelled }


// ---- Finding factories: stable codes; text in locale catalogs ----

public static class OrderFindings
{
    public static readonly FindingFactory AlreadyCompleted = Finding.Error("orders.already-completed");
    public static readonly FindingFactory CannotCompleteCancelled = Finding.Error("orders.cannot-complete-cancelled");
    public static readonly FindingFactory AlreadyCancelled = Finding.Error("orders.already-cancelled");
    public static readonly FindingFactory CannotCancelCompleted = Finding.Error("orders.cannot-cancel-completed");
    public static readonly FindingFactory InvalidCustomer = Finding.Error("orders.invalid-customer");
    public static readonly FindingFactory ProjectRequired = Finding.Error("orders.project-required");
    public static readonly FindingFactory ProjectNotForCustomer = Finding.Error("orders.project-not-for-customer");
    public static readonly FindingFactory NotFound = Finding.Error("orders.not-found");
    public static readonly FindingFactory NotEditable = Finding.Error("orders.not-editable");
}


public sealed class Order : IExtensible, Tam.EntityFrameworkCore.IVersioned, Tam.EntityFrameworkCore.ITenantScoped
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
    public Money? EstimatedTotal { get; private set; }
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
        if (Status == OrderStatus.Completed) return OrderFindings.AlreadyCompleted;
        if (Status == OrderStatus.Cancelled) return OrderFindings.CannotCompleteCancelled;
        Status = OrderStatus.Completed;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == OrderStatus.Cancelled) return OrderFindings.AlreadyCancelled;
        if (Status == OrderStatus.Completed) return OrderFindings.CannotCancelCompleted;
        Status = OrderStatus.Cancelled;
        return Result.Success();
    }
}
