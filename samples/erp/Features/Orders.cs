using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

public static class OrderRules
{
    public static async Task<Result> CustomerCanReceiveOrder(
        CustomerId customerId, ErpDbContext db, CancellationToken ct)
    {
        var customer = await db.Customers
            .Where(x => x.Id == customerId)
            .Select(x => new { x.IsActive })
            .SingleOrDefaultAsync(ct);
        return customer is { IsActive: true } ? Result.Success() : OrderErrors.InvalidCustomer;
    }

    public static async Task<Result> ProjectBelongsToCustomer(
        ProjectId? projectId, CustomerId customerId, ErpDbContext db, CancellationToken ct)
    {
        if (projectId is null) return OrderErrors.ProjectRequired;
        var ok = await db.Projects.AnyAsync(
            x => x.Id == projectId && x.CustomerId == customerId && x.IsOpen, ct);
        return ok ? Result.Success() : OrderErrors.ProjectNotForCustomer;
    }
}

[Operation("orders.create")]
[Authorize("orders.create")]
[AcceptsExtensions(typeof(Order))]
public static class CreateOrder
{
    public sealed record Input(
        [property: LabelKey("labels.customer")] CustomerId CustomerId,
        OrderType OrderType,
        Address WorkAddress,
        OrderDescription Description,
        [property: LabelKey("labels.project")] ProjectId? ProjectId = null,
        DateOnly? RequestedDate = null,
        decimal? EstimatedTotal = null);

    public sealed record Output(OrderId OrderId, OrderNumber Number);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var customerCheck = await OrderRules.CustomerCanReceiveOrder(input.CustomerId, db, ct);
        if (customerCheck.IsError) return customerCheck.As<Output>();

        if (input.OrderType == OrderType.Project)
        {
            var projectCheck = await OrderRules.ProjectBelongsToCustomer(
                input.ProjectId, input.CustomerId, db, ct);
            if (projectCheck.IsError) return projectCheck.As<Output>();
        }

        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;
        var sequence = await db.Orders.CountAsync(ct) + 1412;
        var order = Order.Create(
            context.TenantId.Value,
            new OrderNumber($"{year}-{sequence:D5}"),
            input.CustomerId,
            input.OrderType,
            input.OrderType == OrderType.Project ? input.ProjectId : null,
            input.WorkAddress,
            input.Description,
            input.RequestedDate,
            input.EstimatedTotal);

        db.Orders.Add(order);
        return new Output(order.Id, order.Number);
    }
}

public static class CreateOrderDerivations
{
    [ServerDerivation("orders.create.available-projects")]
    [DependsOn(nameof(CreateOrder.Input.CustomerId), nameof(CreateOrder.Input.OrderType))]
    public static async Task<DerivationResult> AvailableProjects(
        CreateOrder.Input input, DerivationContext context, ErpDbContext db, CancellationToken ct)
    {
        if (input.OrderType != OrderType.Project || input.CustomerId.Value == Guid.Empty)
            return DerivationResult.Empty;

        var options = await db.Projects
            .Where(x => x.CustomerId == input.CustomerId && x.IsOpen)
            .OrderBy(x => x.Name)
            .Select(x => new Option(x.Id.Value, x.Name))
            .ToListAsync(ct);

        return DerivationResult.Empty.AddOptions(nameof(CreateOrder.Input.ProjectId), options);
    }

    [ServerDerivation("orders.create.customer-state")]
    [DependsOn(nameof(CreateOrder.Input.CustomerId))]
    public static async Task<DerivationResult> CustomerState(
        CreateOrder.Input input, DerivationContext context, ErpDbContext db, CancellationToken ct)
    {
        if (input.CustomerId.Value == Guid.Empty) return DerivationResult.Empty;

        var check = await OrderRules.CustomerCanReceiveOrder(input.CustomerId, db, ct);
        if (check.IsError)
            return DerivationResult.From(check, target: nameof(CreateOrder.Input.CustomerId));

        var customer = await db.Customers
            .Where(x => x.Id == input.CustomerId)
            .Select(x => new { x.CreditBlocked, x.VisitAddress })
            .SingleAsync(ct);

        var result = DerivationResult.Empty;
        if (customer.CreditBlocked)
            result = result.AddWarning(CustomerFindings.CreditBlocked);

        return result.Suggest(nameof(CreateOrder.Input.WorkAddress), customer.VisitAddress.Value);
    }
}

[Operation("orders.edit-details")]
[Authorize("orders.edit")]
[AcceptsExtensions(typeof(Order))]
public static class EditOrderDetails
{
    public sealed record Input(
        OrderId OrderId,
        Change<OrderDescription?>? Description = null,
        Change<DateOnly?>? RequestedDate = null,
        Change<Address?>? WorkAddress = null,
        Change<decimal?>? EstimatedTotal = null);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderErrors.NotFound;
        if (order.Status != OrderStatus.Open) return OrderErrors.NotEditable;

        var merge = TamMerge.Apply(order, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();

        return new Output(order.Version + 1);
    }
}

[Operation("orders.complete")]
[Authorize("orders.complete")]
public static class CompleteOrder
{
    public sealed record Input(OrderId OrderId);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderErrors.NotFound;

        var scope = context.CheckOwnership("orders.complete", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        var result = order.Complete();
        if (result.IsError) return result.As<Output>();

        return new Result<Output> { Output = new Output(order.Version + 1) }
            .Effect(new EventPublished("order-completed", new { orderId = order.Id.Value }));
    }
}

[View("orders.list")]
[Authorize("orders.read")]
[AcceptsExtensions(typeof(Order))]
public static class OrderList
{
    // Status/Type filtering is mechanical (declared Filterable); only Search is authored logic.
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public OrderId Id { get; init; }
        public OrderNumber Number { get; init; }
        [LabelKey("labels.customer")]
        public CustomerName CustomerName { get; init; }
        public OrderType Type { get; init; }
        public OrderStatus Status { get; init; }
        public DateOnly? RequestedDate { get; init; }
        public decimal? EstimatedTotal { get; init; }
        public long Version { get; init; }
        public ExtensionData Extensions { get; init; } = new();
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        var orders = db.Orders.ScopedTo(context, "orders.read", x => x.AssignedToActorId);
        if (!string.IsNullOrWhiteSpace(query.Search))
            orders = orders.Where(x =>
                ((string)(object)x.Number).Contains(query.Search!) ||
                ((string)(object)x.Description).Contains(query.Search!));

        return orders
            .Join(db.Customers, o => o.CustomerId, c => c.Id, (o, c) => new Result
            {
                Id = o.Id, Number = o.Number, CustomerName = c.Name, Type = o.Type,
                Status = o.Status, RequestedDate = o.RequestedDate,
                EstimatedTotal = o.EstimatedTotal, Version = o.Version, Extensions = o.Extensions,
            });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Number), nameof(Result.CustomerName), nameof(Result.RequestedDate))
        .Filterable(nameof(Result.Status), nameof(Result.Type), nameof(Result.CustomerName),
            nameof(Result.RequestedDate), nameof(Result.EstimatedTotal))
        .DefaultSort(nameof(Result.Number), descending: true);
}

/// <summary>Detail view backing the edit form: current values + version for the merge base.</summary>
[View("orders.detail")]
[Authorize("orders.read")]
[AcceptsExtensions(typeof(Order))]
public static class OrderDetail
{
    public sealed record Query(OrderId OrderId);

    public sealed record Result
    {
        public OrderId Id { get; init; }
        public OrderNumber Number { get; init; }
        [LabelKey("labels.customer")]
        public CustomerName CustomerName { get; init; }
        public OrderType Type { get; init; }
        public OrderStatus Status { get; init; }
        public Address WorkAddress { get; init; }
        public OrderDescription Description { get; init; }
        public DateOnly? RequestedDate { get; init; }
        public decimal? EstimatedTotal { get; init; }
        public long Version { get; init; }
        public ExtensionData Extensions { get; init; } = new();
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db) =>
        db.Orders.Where(x => x.Id == query.OrderId)
            .Join(db.Customers, o => o.CustomerId, c => c.Id, (o, c) => new Result
            {
                Id = o.Id, Number = o.Number, CustomerName = c.Name, Type = o.Type,
                Status = o.Status, WorkAddress = o.WorkAddress, Description = o.Description,
                RequestedDate = o.RequestedDate, EstimatedTotal = o.EstimatedTotal,
                Version = o.Version, Extensions = o.Extensions,
            });
}
