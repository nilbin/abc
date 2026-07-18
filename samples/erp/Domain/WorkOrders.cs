using Tam;

namespace Erp;

// The WorkOrders aggregate: value types, finding factories, entity (docs/02).


public readonly record struct WorkOrderId(Guid Value);


[LabelKey("labels.work-order-number")]
public readonly record struct WorkOrderNumber(string Value);


[Multiline, MaxLength(1000)]
public readonly record struct WorkDescription(string Value);


public enum WorkOrderStatus { Draft, Scheduled, InProgress, Done, Closed }


// The dispatch priority (docs/02: the type carries the semantics; docs/21: members localize
// as enums.{kebab(value)} model-wide — enums.low / enums.normal / enums.urgent).
public enum WorkOrderPriority { Low, Normal, Urgent }


public static class WorkOrderFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("work-orders.not-found");
    public static readonly FindingFactory ProjectNotOpen = Finding.Error("work-orders.project-not-open");
    public static readonly FindingFactory NotEditable = Finding.Error("work-orders.not-editable");
    public static readonly FindingFactory InvalidTransition = Finding.Error("work-orders.invalid-transition");
    public static readonly FindingFactory ScheduleNeedsAssignee = Finding.Error("work-orders.schedule-needs-assignee");
}


public sealed class WorkOrder : IExtensible, Tam.EntityFrameworkCore.IVersioned, Tam.EntityFrameworkCore.ITenantScoped
{
    private WorkOrder() { }

    public WorkOrderId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public WorkOrderNumber Number { get; private set; }
    public ProjectId ProjectId { get; private set; }
    public string Title { get; private set; } = "";
    public WorkDescription Description { get; private set; }
    public Address Location { get; private set; }
    public DateOnly? ScheduledDate { get; private set; }
    public string? AssignedToActorId { get; private set; }
    // Snapshot of the assignee's display name, taken at assignment time: views render it
    // without a cross-provider join into the framework's account table (docs/34 friction log).
    public string? AssignedToName { get; private set; }
    public WorkOrderStatus Status { get; private set; }
    public WorkOrderPriority Priority { get; private set; }
    public long Version { get; set; }
    public ExtensionData Extensions { get; set; } = new();

    public static WorkOrder Create(
        string tenantId, WorkOrderNumber number, ProjectId projectId, string title,
        WorkDescription description, Address location,
        WorkOrderPriority priority = WorkOrderPriority.Normal) => new()
    {
        Id = new WorkOrderId(Guid.NewGuid()),
        TenantId = tenantId,
        Number = number,
        ProjectId = projectId,
        Title = title,
        Description = description,
        Location = location,
        Status = WorkOrderStatus.Draft,
        Priority = priority,
    };

    // The machine: Draft → Scheduled → InProgress → Done → Closed. Every arrow is an
    // INTENT operation (EDIT001); details are editable only before work starts.
    public bool IsEditable => Status is WorkOrderStatus.Draft or WorkOrderStatus.Scheduled;

    public Result Schedule(DateOnly date, string assigneeActorId, string assigneeName)
    {
        if (Status is not (WorkOrderStatus.Draft or WorkOrderStatus.Scheduled))
            return WorkOrderFindings.InvalidTransition;
        ScheduledDate = date;
        AssignedToActorId = assigneeActorId;
        AssignedToName = assigneeName;
        Status = WorkOrderStatus.Scheduled;
        return Result.Success();
    }

    public Result Start()
    {
        if (Status != WorkOrderStatus.Scheduled) return WorkOrderFindings.InvalidTransition;
        Status = WorkOrderStatus.InProgress;
        return Result.Success();
    }

    public Result Complete()
    {
        if (Status != WorkOrderStatus.InProgress) return WorkOrderFindings.InvalidTransition;
        Status = WorkOrderStatus.Done;
        return Result.Success();
    }

    public Result CloseOut()
    {
        if (Status != WorkOrderStatus.Done) return WorkOrderFindings.InvalidTransition;
        Status = WorkOrderStatus.Closed;
        return Result.Success();
    }

    // Priority is consequential state (an automation rule reads it at schedule time), so it
    // moves through this intent — EDIT001 bans it from the generic change-set. Same window
    // as detail edits: set it while Draft/Scheduled, frozen once work starts.
    public Result SetPriority(WorkOrderPriority priority)
    {
        if (!IsEditable) return WorkOrderFindings.NotEditable;
        Priority = priority;
        return Result.Success();
    }

    public Result Reassign(string assigneeActorId, string assigneeName)
    {
        if (Status is WorkOrderStatus.Done or WorkOrderStatus.Closed)
            return WorkOrderFindings.InvalidTransition;
        AssignedToActorId = assigneeActorId;
        AssignedToName = assigneeName;
        return Result.Success();
    }
}


// The aggregate's published language (docs/31 "events are records").
[DomainEvent("work-order-completed")]
public sealed record WorkOrderCompleted(Guid WorkOrderId, string Number);
