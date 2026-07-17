using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

public static class WorkOrderRules
{
    /// <summary>Resolves an assignee against THIS tenant's memberships and returns the display
    /// name to snapshot. Views never join the framework's account table (docs/34 friction log:
    /// no framework story for rendering actor references — the name is denormalized at
    /// assignment time instead).</summary>
    public static async Task<(Result Result, string Name)> ResolveAssignee(
        string assigneeActorId, ErpDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(assigneeActorId, out var accountId))
            return (WorkOrderFindings.ScheduleNeedsAssignee.Create(), "");
        var member = await db.Set<TenantMembershipEntity>()
            .AnyAsync(m => m.AccountId == accountId && m.Active, ct);
        if (!member) return (WorkOrderFindings.ScheduleNeedsAssignee.Create(), "");
        var name = await db.Set<AccountEntity>()
            .Where(a => a.Id == accountId)
            .Select(a => a.DisplayName)
            .SingleAsync(ct);
        return (Result.Success(), name);
    }
}

[Operation("work-orders.create")]
[Authorize("work-orders.create")]
[AcceptsExtensions(typeof(WorkOrder))]
public static class CreateWorkOrder
{
    public sealed record Input(
        ProjectId ProjectId,
        string Title,
        WorkDescription Description,
        Address Location,
        // Optional on the wire (D4-additive); the domain default is Normal.
        WorkOrderPriority Priority = WorkOrderPriority.Normal);

    public sealed record Output(WorkOrderId WorkOrderId, WorkOrderNumber Number);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var projectOpen = await db.Projects.AnyAsync(
            x => x.Id == input.ProjectId && x.Status == ProjectStatus.Open, ct);
        if (!projectOpen)
            return WorkOrderFindings.ProjectNotOpen.At(nameof(Input.ProjectId));

        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;
        var sequence = await db.WorkOrders.CountAsync(ct) + 1;
        var workOrder = WorkOrder.Create(
            context.TenantId.Value,
            new WorkOrderNumber($"WO-{year}-{sequence:D4}"),
            input.ProjectId,
            input.Title,
            input.Description,
            input.Location,
            input.Priority);
        db.WorkOrders.Add(workOrder);
        return new Output(workOrder.Id, workOrder.Number);
    }
}

[Operation("work-orders.edit-details")]
[Authorize("work-orders.edit")]
[Widens("work-orders.edit-all")]
[AcceptsExtensions(typeof(WorkOrder))]
public static class EditWorkOrderDetails
{
    public sealed record Input(
        [property: LabelKey("labels.work-order")] WorkOrderId WorkOrderId,
        Change<string>? Title = null,
        Change<WorkDescription>? Description = null,
        Change<Address>? Location = null);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == input.WorkOrderId, ct);
        if (workOrder is null) return WorkOrderFindings.NotFound.Create();

        var scope = context.CheckOwnershipUnless("work-orders.edit-all", workOrder.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        if (!workOrder.IsEditable) return WorkOrderFindings.NotEditable.Create();

        var merge = TamMerge.Apply(workOrder, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();

        return new Output(workOrder.Version + 1);
    }
}

/// <summary>Scheduling assigns AND dates in one intent — a work order on the board without
/// an owner is not "scheduled" in this domain.</summary>
[Operation("work-orders.schedule")]
[Authorize("work-orders.schedule")]
public static class ScheduleWorkOrder
{
    public sealed record Input(
        [property: LabelKey("labels.work-order")] WorkOrderId WorkOrderId,
        DateOnly ScheduledDate,
        [property: LabelKey("labels.assignee"), Lookup("users.lookup")] string AssigneeActorId);

    public sealed record Output(WorkOrderStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == input.WorkOrderId, ct);
        if (workOrder is null) return WorkOrderFindings.NotFound.Create();

        var (assignee, name) = await WorkOrderRules.ResolveAssignee(input.AssigneeActorId, db, ct);
        if (assignee.IsError) return assignee.As<Output>();

        var result = workOrder.Schedule(input.ScheduledDate, input.AssigneeActorId, name);
        if (result.IsError) return result.As<Output>();
        return new Output(workOrder.Status);
    }
}

/// <summary>Priority is an enum, so EDIT001 keeps it OFF the generic change-set: re-prioritizing
/// is an intent of its own, guarded by the same editability window as edit-details.</summary>
[Operation("work-orders.set-priority")]
[Authorize("work-orders.edit")]
[Widens("work-orders.edit-all")]
public static class SetWorkOrderPriority
{
    public sealed record Input(
        [property: LabelKey("labels.work-order")] WorkOrderId WorkOrderId,
        WorkOrderPriority Priority);

    public sealed record Output(WorkOrderPriority Priority);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == input.WorkOrderId, ct);
        if (workOrder is null) return WorkOrderFindings.NotFound.Create();

        var scope = context.CheckOwnershipUnless("work-orders.edit-all", workOrder.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = workOrder.SetPriority(input.Priority);
        if (result.IsError) return result.As<Output>();
        return new Output(workOrder.Priority);
    }
}

[Operation("work-orders.assign")]
[Authorize("work-orders.assign")]
public static class AssignWorkOrder
{
    public sealed record Input(
        [property: LabelKey("labels.work-order")] WorkOrderId WorkOrderId,
        [property: LabelKey("labels.assignee"), Lookup("users.lookup")] string AssigneeActorId);

    public sealed record Output(WorkOrderStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == input.WorkOrderId, ct);
        if (workOrder is null) return WorkOrderFindings.NotFound.Create();

        var (assignee, name) = await WorkOrderRules.ResolveAssignee(input.AssigneeActorId, db, ct);
        if (assignee.IsError) return assignee.As<Output>();

        var result = workOrder.Reassign(input.AssigneeActorId, name);
        if (result.IsError) return result.As<Output>();
        return new Output(workOrder.Status);
    }
}

[Operation("work-orders.start")]
[Authorize("work-orders.start")]
[Widens("work-orders.start-all")]
public static class StartWorkOrder
{
    public sealed record Input([property: LabelKey("labels.work-order")] WorkOrderId WorkOrderId);

    public sealed record Output(WorkOrderStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == input.WorkOrderId, ct);
        if (workOrder is null) return WorkOrderFindings.NotFound.Create();

        var scope = context.CheckOwnershipUnless("work-orders.start-all", workOrder.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = workOrder.Start();
        if (result.IsError) return result.As<Output>();
        return new Output(workOrder.Status);
    }
}

[Operation("work-orders.complete")]
[Authorize("work-orders.complete")]
[Widens("work-orders.complete-all")]
public static class CompleteWorkOrder
{
    public sealed record Input([property: LabelKey("labels.work-order")] WorkOrderId WorkOrderId);

    public sealed record Output(WorkOrderStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == input.WorkOrderId, ct);
        if (workOrder is null) return WorkOrderFindings.NotFound.Create();

        var scope = context.CheckOwnershipUnless("work-orders.complete-all", workOrder.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = workOrder.Complete();
        if (result.IsError) return result.As<Output>();

        // The M4 seam: invoicing will draft from completed work (docs/34) — same contract
        // shape as order-completed (docs/31 D-X5).
        return new Result<Output> { Output = new Output(workOrder.Status) }
            .Effect(new EventPublished("work-order-completed",
                new { workOrderId = workOrder.Id.Value, number = workOrder.Number.Value }));
    }
}

[Operation("work-orders.close")]
[Authorize("work-orders.close")]
public static class CloseWorkOrder
{
    public sealed record Input([property: LabelKey("labels.work-order")] WorkOrderId WorkOrderId);

    public sealed record Output(WorkOrderStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == input.WorkOrderId, ct);
        if (workOrder is null) return WorkOrderFindings.NotFound.Create();

        var result = workOrder.CloseOut();
        if (result.IsError) return result.As<Output>();
        return new Output(workOrder.Status);
    }
}

[View("work-orders.list")]
[Authorize("work-orders.read")]
[Widens("work-orders.read-all")]
[AcceptsExtensions(typeof(WorkOrder))]
public static class WorkOrderList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public WorkOrderId Id { get; init; }
        public WorkOrderNumber Number { get; init; }
        public string Title { get; init; } = "";
        public ProjectNumber ProjectNumber { get; init; }
        public WorkOrderStatus Status { get; init; }
        public WorkOrderPriority Priority { get; init; }
        public DateOnly? ScheduledDate { get; init; }
        [LabelKey("labels.assignee")]
        public string? AssignedToName { get; init; }
        [LabelKey("labels.company")]
        public string TenantId { get; init; } = "";
        public long Version { get; init; }
        public ExtensionData Extensions { get; init; } = new();
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        // Same composition rule as orders.list: the join widens, so the work-order side
        // scopes itself explicitly; technicians see their own unless read-all widens.
        var workOrders = db.WorkOrders.InScope(db, context.TenantId)
            .ScopedUnless(context, "work-orders.read-all", x => x.AssignedToActorId);
        if (!string.IsNullOrWhiteSpace(query.Search))
            workOrders = workOrders.Where(x =>
                ((string)(object)x.Number).Contains(query.Search!) ||
                x.Title.Contains(query.Search!));

        return workOrders
            .Join(db.Projects.InScope(db, context.TenantId),
                w => w.ProjectId, p => p.Id, (w, p) => new Result
            {
                Id = w.Id, Number = w.Number, Title = w.Title, ProjectNumber = p.Number,
                Status = w.Status, Priority = w.Priority, ScheduledDate = w.ScheduledDate,
                AssignedToName = w.AssignedToName, TenantId = w.TenantId,
                Version = w.Version, Extensions = w.Extensions,
            });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Number), nameof(Result.ScheduledDate), nameof(Result.Title))
        .Filterable(nameof(Result.Status), nameof(Result.Priority), nameof(Result.ScheduledDate),
            nameof(Result.ProjectNumber), nameof(Result.AssignedToName))
        .SubtreeRead(nameof(Result.TenantId))
        .DefaultSort(nameof(Result.Number), descending: true);
}

/// <summary>The record surface behind the declared work-orders page: fields named to
/// prefill the edit form.</summary>
[View("work-orders.detail")]
[Authorize("work-orders.read")]
[Widens("work-orders.read-all")]
[AcceptsExtensions(typeof(WorkOrder))]
public static class WorkOrderDetail
{
    public sealed record Query(WorkOrderId WorkOrderId);

    public sealed record Result
    {
        public WorkOrderId Id { get; init; }
        public WorkOrderNumber Number { get; init; }
        public string Title { get; init; } = "";
        public ProjectNumber ProjectNumber { get; init; }
        public WorkDescription Description { get; init; }
        public Address Location { get; init; }
        public WorkOrderStatus Status { get; init; }
        public WorkOrderPriority Priority { get; init; }
        public DateOnly? ScheduledDate { get; init; }
        [LabelKey("labels.assignee")]
        public string? AssignedToName { get; init; }
        public long Version { get; init; }
        public ExtensionData Extensions { get; init; } = new();
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context) =>
        db.WorkOrders.InNode(context.TenantId).Where(x => x.Id == query.WorkOrderId)
            .ScopedUnless(context, "work-orders.read-all", x => x.AssignedToActorId)
            .Join(db.Projects.InScope(db, context.TenantId),
                w => w.ProjectId, p => p.Id, (w, p) => new Result
            {
                Id = w.Id, Number = w.Number, Title = w.Title, ProjectNumber = p.Number,
                Description = w.Description, Location = w.Location, Status = w.Status,
                Priority = w.Priority, ScheduledDate = w.ScheduledDate,
                AssignedToName = w.AssignedToName,
                Version = w.Version, Extensions = w.Extensions,
            });
}
