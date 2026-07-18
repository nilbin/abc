using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

// The Orders aggregate's LIFECYCLE intents (docs/34, merged model): every arrow of
// Open → Scheduled → InProgress → Completed | Cancelled is an intent operation (EDIT001);
// the entity guards the transitions, the operations add scope and the committed-fact events.

[Operation("orders.complete")]
[Authorize("orders.complete")]
[Widens("orders.complete-all")]
public static class CompleteOrder
{
    public sealed record Input(OrderId OrderId);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;

        var scope = context.CheckOwnershipUnless("orders.complete-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = order.Complete();
        if (result.IsError) return result.As<Output>();

        return new Result<Output> { Output = new Output(order.Version + 1) }
            .Effect(new EventPublished(new OrderCompleted(order.Id.Value, order.Number.Value)));
    }
}

/// <summary>The status machine's other exit: Open → Cancelled. The entity guards both arrows
/// (a completed order never cancels, a cancelled one never completes) — the operation only
/// adds scope and the committed-fact event, exactly like orders.complete.</summary>
[Operation("orders.cancel")]
[Authorize("orders.cancel")]
[Widens("orders.cancel-all")]
public static class CancelOrder
{
    public sealed record Input(OrderId OrderId);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;

        var scope = context.CheckOwnershipUnless("orders.cancel-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = order.Cancel();
        if (result.IsError) return result.As<Output>();

        return new Result<Output> { Output = new Output(order.Version + 1) }
            .Effect(new EventPublished(new OrderCancelled(order.Id.Value, order.Number.Value)));
    }
}

/// <summary>Scheduling assigns AND dates in one intent — an order on the board without an
/// owner is not "scheduled" in this domain (the merged execution machine's first arrow).</summary>
[Operation("orders.schedule")]
[Authorize("orders.schedule")]
public static class ScheduleOrder
{
    public sealed record Input(
        OrderId OrderId,
        DateOnly ScheduledDate,
        [property: LabelKey("labels.assignee"), Lookup("users.lookup")] string AssigneeActorId);

    public sealed record Output(OrderStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;

        var (assignee, name) = await OrderRules.ResolveAssignee(input.AssigneeActorId, db, ct);
        if (assignee.IsError) return assignee.As<Output>();

        var result = order.Schedule(input.ScheduledDate, input.AssigneeActorId, name);
        if (result.IsError) return result.As<Output>();
        return new Output(order.Status);
    }
}

[Operation("orders.assign")]
[Authorize("orders.assign")]
public static class AssignOrder
{
    public sealed record Input(
        OrderId OrderId,
        [property: LabelKey("labels.assignee"), Lookup("users.lookup")] string AssigneeActorId);

    public sealed record Output(OrderStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;

        var (assignee, name) = await OrderRules.ResolveAssignee(input.AssigneeActorId, db, ct);
        if (assignee.IsError) return assignee.As<Output>();

        var result = order.Reassign(input.AssigneeActorId, name);
        if (result.IsError) return result.As<Output>();
        return new Output(order.Status);
    }
}

[Operation("orders.start")]
[Authorize("orders.start")]
[Widens("orders.start-all")]
public static class StartOrder
{
    public sealed record Input(OrderId OrderId);

    public sealed record Output(OrderStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;

        var scope = context.CheckOwnershipUnless("orders.start-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = order.Start();
        if (result.IsError) return result.As<Output>();
        return new Output(order.Status);
    }
}

/// <summary>Priority is an enum, so EDIT001 keeps it OFF the generic change-set: re-prioritizing
/// is an intent of its own, guarded by the same editability window as edit-details.</summary>
[Operation("orders.set-priority")]
[Authorize("orders.edit")]
[Widens("orders.edit-all")]
public static class SetOrderPriority
{
    public sealed record Input(OrderId OrderId, OrderPriority Priority);

    public sealed record Output(OrderPriority Priority);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;

        var scope = context.CheckOwnershipUnless("orders.edit-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = order.SetPriority(input.Priority);
        if (result.IsError) return result.As<Output>();
        return new Output(order.Priority);
    }
}
