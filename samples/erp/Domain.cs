using Tam;

namespace Erp;

// ---- Semantic value types (labels live in locales/, never here — docs/21) ----

// Reference types carry their PICKER (docs/34 M5 — the type carries the defaults): declare
// the lookup once, and every form field of this type renders a searchable select over the
// view. [LabelKey] rides the same channel where the convention key would mislead.
[LabelKey("labels.customer"), Lookup("customers.lookup")]
public readonly record struct CustomerId(Guid Value);

[LabelKey("labels.project"), Lookup("projects.lookup")]
public readonly record struct ProjectId(Guid Value);

public readonly record struct OrderId(Guid Value);

[LabelKey("labels.stock-item"), Lookup("stock.lookup")]
public readonly record struct StockItemId(Guid Value);

public readonly record struct WorkOrderId(Guid Value);
public readonly record struct TimeEntryId(Guid Value);
public readonly record struct MaterialLineId(Guid Value);

public readonly record struct CustomerName(string Value);
public readonly record struct OrderNumber(string Value);

// labels.number is already claimed by orders ("Order number") — the flat label namespace
// makes same-named members collide across aggregates, so this type carries its own key
// (docs/34 friction log).
[LabelKey("labels.project-number")]
public readonly record struct ProjectNumber(string Value);

[LabelKey("labels.sku")]
public readonly record struct Sku(string Value);

[LabelKey("labels.work-order-number")]
public readonly record struct WorkOrderNumber(string Value);

[Multiline, MaxLength(1000)]
public readonly record struct WorkDescription(string Value);

[Multiline, MaxLength(500)]
public readonly record struct TimeNote(string Value);

[Multiline, MaxLength(1000)]
public readonly record struct OrderDescription(string Value);

[Format("email")]
public readonly record struct EmailAddress(string Value);

[Format("phone")]
public readonly record struct PhoneNumber(string Value);

public readonly record struct Address(string Value);

public enum OrderType { Service, Project }

public enum OrderStatus { Open, Completed, Cancelled }

public enum ProjectStatus { Open, Closed }

public enum StockUnit { Piece, Hour, Meter, Kilogram, Litre }

public enum WorkOrderStatus { Draft, Scheduled, InProgress, Done, Closed }

public enum TimeEntryStatus { Draft, Approved }

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

public static class ProjectFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("projects.not-found");
    public static readonly FindingFactory DuplicateNumber = Finding.Error("projects.duplicate-number");
    public static readonly FindingFactory NotOpen = Finding.Error("projects.not-open");
    public static readonly FindingFactory NotClosed = Finding.Error("projects.not-closed");
    public static readonly FindingFactory HasOpenOrders = Finding.Error("projects.has-open-orders");
}

public static class StockFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("stock.not-found");
    public static readonly FindingFactory DuplicateSku = Finding.Error("stock.duplicate-sku");
}

public static class TimeFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("time.not-found");
    public static readonly FindingFactory InvalidHours = Finding.Error("time.invalid-hours");
    public static readonly FindingFactory InvalidRate = Finding.Error("time.invalid-rate");
    public static readonly FindingFactory AlreadyApproved = Finding.Error("time.already-approved");
    public static readonly FindingFactory WorkOrderClosed = Finding.Error("time.work-order-closed");
}

public static class MaterialFindings
{
    public static readonly FindingFactory InvalidQuantity = Finding.Error("materials.invalid-quantity");
    public static readonly FindingFactory StockItemInactive = Finding.Error("materials.stock-item-inactive");
    public static readonly FindingFactory WorkOrderClosed = Finding.Error("materials.work-order-closed");
}

public static class WorkOrderFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("work-orders.not-found");
    public static readonly FindingFactory ProjectNotOpen = Finding.Error("work-orders.project-not-open");
    public static readonly FindingFactory NotEditable = Finding.Error("work-orders.not-editable");
    public static readonly FindingFactory InvalidTransition = Finding.Error("work-orders.invalid-transition");
    public static readonly FindingFactory ScheduleNeedsAssignee = Finding.Error("work-orders.schedule-needs-assignee");
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

    public void Deactivate() => IsActive = false;
}

public sealed class Project : Tam.EntityFrameworkCore.ITenantScoped
{
    private Project() { }

    public ProjectId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public ProjectNumber Number { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public string Name { get; private set; } = "";
    public Money? Budget { get; private set; }
    public ProjectStatus Status { get; private set; }

    public static Project Create(
        string tenantId, ProjectNumber number, CustomerId customerId, string name,
        decimal? budget = null) => new()
    {
        Id = new ProjectId(Guid.NewGuid()),
        TenantId = tenantId,
        Number = number,
        CustomerId = customerId,
        Name = name,
        Budget = budget,
        Status = ProjectStatus.Open,
    };

    // The open-orders guard needs the database, so it lives in the operation; the entity
    // only protects its own state machine.
    public Result Close()
    {
        if (Status == ProjectStatus.Closed) return ProjectFindings.NotOpen;
        Status = ProjectStatus.Closed;
        return Result.Success();
    }

    public Result Reopen()
    {
        if (Status != ProjectStatus.Closed) return ProjectFindings.NotClosed;
        Status = ProjectStatus.Open;
        return Result.Success();
    }
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
    public long Version { get; set; }
    public ExtensionData Extensions { get; set; } = new();

    public static WorkOrder Create(
        string tenantId, WorkOrderNumber number, ProjectId projectId, string title,
        WorkDescription description, Address location) => new()
    {
        Id = new WorkOrderId(Guid.NewGuid()),
        TenantId = tenantId,
        Number = number,
        ProjectId = projectId,
        Title = title,
        Description = description,
        Location = location,
        Status = WorkOrderStatus.Draft,
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

    public Result Reassign(string assigneeActorId, string assigneeName)
    {
        if (Status is WorkOrderStatus.Done or WorkOrderStatus.Closed)
            return WorkOrderFindings.InvalidTransition;
        AssignedToActorId = assigneeActorId;
        AssignedToName = assigneeName;
        return Result.Success();
    }
}

public sealed class StockItem : Tam.EntityFrameworkCore.ITenantScoped
{
    private StockItem() { }

    public StockItemId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Sku Sku { get; private set; }
    public string Name { get; private set; } = "";
    public StockUnit Unit { get; private set; }
    public Money UnitPrice { get; private set; }
    public bool IsActive { get; private set; }

    public static StockItem Create(
        string tenantId, Sku sku, string name, StockUnit unit, decimal unitPrice) => new()
    {
        Id = new StockItemId(Guid.NewGuid()),
        TenantId = tenantId,
        Sku = sku,
        Name = name,
        Unit = unit,
        UnitPrice = unitPrice,
        IsActive = true,
    };

    public void Deactivate() => IsActive = false;
}

/// <summary>A technician's time booking on a work order (docs/34 M3). Owned by the BOOKING
/// technician — the paired-atom OWN scope rides TechnicianActorId. Amount is computed and
/// STORED at booking time (hours × rate), so later rate conventions never rewrite history;
/// approval is an intent operation (EDIT001), never an edit.</summary>
public sealed class TimeEntry : Tam.EntityFrameworkCore.ITenantScoped
{
    private TimeEntry() { }

    public TimeEntryId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public WorkOrderId WorkOrderId { get; private set; }
    public string TechnicianActorId { get; private set; } = "";
    // Snapshot of the technician's display name at booking time — same denormalization as
    // WorkOrder.AssignedToName (docs/34 friction log: no actor-reference rendering story).
    public string TechnicianName { get; private set; } = "";
    public DateOnly Date { get; private set; }
    public decimal Hours { get; private set; }
    public Money HourlyRate { get; private set; }
    public Money Amount { get; private set; }
    public TimeNote? Note { get; private set; }
    public TimeEntryStatus Status { get; private set; }

    public static TimeEntry Book(
        string tenantId, WorkOrderId workOrderId, string technicianActorId, string technicianName,
        DateOnly date, decimal hours, decimal hourlyRate, TimeNote? note) => new()
    {
        Id = new TimeEntryId(Guid.NewGuid()),
        TenantId = tenantId,
        WorkOrderId = workOrderId,
        TechnicianActorId = technicianActorId,
        TechnicianName = technicianName,
        Date = date,
        Hours = hours,
        HourlyRate = hourlyRate,
        Amount = decimal.Round(hours * hourlyRate, 2),
        Note = note,
        Status = TimeEntryStatus.Draft,
    };

    public Result Approve()
    {
        if (Status == TimeEntryStatus.Approved) return TimeFindings.AlreadyApproved;
        Status = TimeEntryStatus.Approved;
        return Result.Success();
    }
}

/// <summary>Stock consumption on a work order (docs/34 M3). UnitPrice is a SNAPSHOT of the
/// stock item's price at entry time — catalog price changes never rewrite booked history —
/// and Amount (quantity × snapshot price) is stored with it.</summary>
public sealed class MaterialLine : Tam.EntityFrameworkCore.ITenantScoped
{
    private MaterialLine() { }

    public MaterialLineId Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public WorkOrderId WorkOrderId { get; private set; }
    public StockItemId StockItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money Amount { get; private set; }

    public static MaterialLine Add(
        string tenantId, WorkOrderId workOrderId, StockItemId stockItemId,
        decimal quantity, decimal unitPriceSnapshot) => new()
    {
        Id = new MaterialLineId(Guid.NewGuid()),
        TenantId = tenantId,
        WorkOrderId = workOrderId,
        StockItemId = stockItemId,
        Quantity = quantity,
        UnitPrice = unitPriceSnapshot,
        Amount = decimal.Round(quantity * unitPriceSnapshot, 2),
    };
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
        if (Status == OrderStatus.Completed) return OrderErrors.AlreadyCompleted;
        if (Status == OrderStatus.Cancelled) return OrderErrors.CannotCompleteCancelled;
        Status = OrderStatus.Completed;
        return Result.Success();
    }
}
