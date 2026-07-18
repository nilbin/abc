using Tam;

namespace Erp;

// The Orders aggregate: value types, finding factories, entity (docs/02).
//
// ONE entity for the whole engagement (docs/34, merged): the commercial commitment and its
// execution are the same order — what the customer asked for, and the scheduled, assigned,
// prioritized job the technician runs. The old Order/WorkOrder split modeled them apart and
// left them unlinked; the merge folds the execution machine (schedule → start → complete)
// into the commercial lifecycle. Multi-visit engagements are what PROJECTS group.


public readonly record struct OrderId(Guid Value);

public readonly record struct OrderNumber(string Value);


[Multiline, MaxLength(1000)]
public readonly record struct OrderDescription(string Value);


public enum OrderType { Service, Project }


// Open → Scheduled → InProgress → Completed, with Cancelled reachable from every
// non-terminal state and Completed reachable early (a pure sales order needs no scheduling).
public enum OrderStatus { Open, Scheduled, InProgress, Completed, Cancelled }


// The dispatch priority (docs/02: the type carries the semantics; docs/21: members localize
// as enums.{kebab(value)} model-wide — enums.low / enums.normal / enums.urgent).
public enum OrderPriority { Low, Normal, Urgent }


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
    public static readonly FindingFactory InvalidTransition = Finding.Error("orders.invalid-transition");
    public static readonly FindingFactory ScheduleNeedsAssignee = Finding.Error("orders.schedule-needs-assignee");
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
    public OrderPriority Priority { get; private set; }
    public DateOnly? ScheduledDate { get; private set; }
    public string? AssignedToActorId { get; private set; }
    // Snapshot of the assignee's display name, taken at assignment time: views render it
    // without a cross-provider join into the framework's account table (docs/34 friction log).
    public string? AssignedToName { get; private set; }
    public long Version { get; set; }
    public ExtensionData Extensions { get; set; } = new();

    public static Order Create(
        string tenantId, OrderNumber number, CustomerId customerId, OrderType type,
        ProjectId? projectId, Address workAddress, OrderDescription description,
        DateOnly? requestedDate, decimal? estimatedTotal,
        OrderPriority priority = OrderPriority.Normal) => new()
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
        Priority = priority,
    };

    // Details are editable until work starts; terminal states are read-only history.
    public bool IsEditable => Status is OrderStatus.Open or OrderStatus.Scheduled;

    private bool IsTerminal => Status is OrderStatus.Completed or OrderStatus.Cancelled;

    /// <summary>Scheduling assigns AND dates in one intent — an order on the board without an
    /// owner is not "scheduled" in this domain. Re-scheduling while still Scheduled is fine.</summary>
    public Result Schedule(DateOnly date, string assigneeActorId, string assigneeName)
    {
        if (Status is not (OrderStatus.Open or OrderStatus.Scheduled))
            return OrderFindings.InvalidTransition;
        ScheduledDate = date;
        AssignedToActorId = assigneeActorId;
        AssignedToName = assigneeName;
        Status = OrderStatus.Scheduled;
        return Result.Success();
    }

    public Result Start()
    {
        if (Status != OrderStatus.Scheduled) return OrderFindings.InvalidTransition;
        Status = OrderStatus.InProgress;
        return Result.Success();
    }

    /// <summary>Completion is legal from every live state — a pure sales order completes
    /// without ever being scheduled; a field job completes out of InProgress.</summary>
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

    public Result Reassign(string assigneeActorId, string assigneeName)
    {
        if (IsTerminal) return OrderFindings.InvalidTransition;
        AssignedToActorId = assigneeActorId;
        AssignedToName = assigneeName;
        return Result.Success();
    }

    // Priority is consequential state (an automation rule reads it at schedule time), so it
    // moves through this intent — EDIT001 bans it from the generic change-set. Same window
    // as detail edits: set it while Open/Scheduled, frozen once work starts.
    public Result SetPriority(OrderPriority priority)
    {
        if (!IsEditable) return OrderFindings.NotEditable;
        Priority = priority;
        return Result.Success();
    }
}


// ---- The aggregate's published language (docs/31 "events are records"): the record IS the
// contract — fields and kinds derive from its members, discovery registers it, and the
// publish site is compile-checked (TAM009 refuses anonymous payloads). ----

[DomainEvent("order-created")]
public sealed record OrderCreated(Guid OrderId, string Number, OrderType OrderType);

[DomainEvent("order-completed")]
public sealed record OrderCompleted(Guid OrderId, string Number);

[DomainEvent("order-cancelled")]
public sealed record OrderCancelled(Guid OrderId, string Number);
