using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

public static class OrderRules
{
    public static async Task<Result> CustomerCanReceiveOrder(
        CustomerId customerId, TenantId tenant, ErpDbContext db, CancellationToken ct)
    {
        // Customers are a shared registry down the group (docs/27 inherited): an order may reference
        // this node's own customers AND ancestor-owned ones — the same set the lookup view offers.
        var customer = await db.Customers.WithInherited(db, tenant)
            .Where(x => x.Id == customerId)
            .Select(x => new { x.IsActive })
            .SingleOrDefaultAsync(ct);
        return customer is { IsActive: true } ? Result.Success() : OrderFindings.InvalidCustomer;
    }

    public static async Task<Result> ProjectBelongsToCustomer(
        ProjectId? projectId, CustomerId customerId, ErpDbContext db, CancellationToken ct)
    {
        if (projectId is null) return OrderFindings.ProjectRequired;
        var ok = await db.Projects.AnyAsync(
            x => x.Id == projectId && x.CustomerId == customerId && x.Status == ProjectStatus.Open, ct);
        return ok ? Result.Success() : OrderFindings.ProjectNotForCustomer;
    }

    /// <summary>Resolves an assignee against THIS tenant's memberships and returns the display
    /// name to snapshot. Views never join the framework's account table (docs/34 friction log:
    /// no framework story for rendering actor references — the name is denormalized at
    /// assignment time instead).</summary>
    public static async Task<(Result Result, string Name)> ResolveAssignee(
        string assigneeActorId, ErpDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(assigneeActorId, out var accountId))
            return (OrderFindings.ScheduleNeedsAssignee.Create(), "");
        var member = await db.Set<TenantMembershipEntity>()
            .AnyAsync(m => m.AccountId == accountId && m.Active, ct);
        if (!member) return (OrderFindings.ScheduleNeedsAssignee.Create(), "");
        var name = await db.Set<AccountEntity>()
            .Where(a => a.Id == accountId)
            .Select(a => a.DisplayName)
            .SingleAsync(ct);
        return (Result.Success(), name);
    }
}

public static class OrderNumbering
{
    // The first order for a tenant that has no counter row yet. Matches the demo's historical
    // base so seeded numbers (…-01412) and freshly-created ones share one sequence.
    private const int FirstNumber = 1412;

    /// <summary>Atomically claims the next order number for the tenant, INSIDE the operation's
    /// transaction (Sol review, Finding 5). The UPDATE takes the counter row's write-lock — held
    /// to commit — so a concurrent create BLOCKS on it rather than reading a stale value; the
    /// number is unique, monotonic, and never recycled by a delete.</summary>
    public static async Task<int> NextAsync(ErpDbContext db, string tenant, CancellationToken ct)
    {
        // No manual TenantId predicate: OrderNumberSequence is ITenantScoped, so the global query
        // filter already scopes this to the current tenant's single counter row (Sol re-review,
        // Finding 5A — and a manual filter would be TAM004). The UPDATE takes that row's write-lock.
        var bumped = await db.Set<OrderNumberSequence>()
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Next, x => x.Next + 1), ct);
        if (bumped == 0)
        {
            // No counter for this tenant yet (created after seed). Claim the first number; a
            // concurrent first-create loses the PK race, surfaces as a version conflict, and
            // retries into the UPDATE path above — so no duplicate is ever committed. TenantId is
            // set here and re-stamped by the SaveChanges interceptor; both agree on the tenant.
            db.Set<OrderNumberSequence>().Add(new OrderNumberSequence { TenantId = tenant, Next = FirstNumber });
            return FirstNumber;
        }
        return await db.Set<OrderNumberSequence>()
            .Select(x => x.Next)
            .SingleAsync(ct);
    }
}

[Operation("orders.create")]
[Authorize("orders.create")]
[AcceptsExtensions(typeof(Order))]
public static class CreateOrder
{
    public sealed record Input(
        CustomerId CustomerId,
        OrderType OrderType,
        Address WorkAddress,
        OrderDescription Description,
        ProjectId? ProjectId = null,
        DateOnly? RequestedDate = null,
        Money? EstimatedTotal = null,
        // Optional on the wire (D4-additive); the domain default is Normal.
        OrderPriority Priority = OrderPriority.Normal);

    public sealed record Output(OrderId OrderId, OrderNumber Number);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var customerCheck = await OrderRules.CustomerCanReceiveOrder(
            input.CustomerId, context.TenantId, db, ct);
        if (customerCheck.IsError) return customerCheck.As<Output>();

        if (input.OrderType == OrderType.Project)
        {
            var projectCheck = await OrderRules.ProjectBelongsToCustomer(
                input.ProjectId, input.CustomerId, db, ct);
            if (projectCheck.IsError) return projectCheck.As<Output>();
        }

        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;
        var sequence = await OrderNumbering.NextAsync(db, context.TenantId.Value, ct);
        var order = Order.Create(
            context.TenantId.Value,
            new OrderNumber($"{year}-{sequence:D5}"),
            input.CustomerId,
            input.OrderType,
            input.OrderType == OrderType.Project ? input.ProjectId : null,
            input.WorkAddress,
            input.Description,
            input.RequestedDate,
            input.EstimatedTotal,
            input.Priority);

        db.Orders.Add(order);
        // The creation is a committed fact other modules build on (docs/31 D-X5): the event
        // carries the wire keys a subscriber needs — the inspect plugin instantiates matching
        // checklist templates from orderType. The wire value ("service"/"project"), never the
        // CLR enum, is the contract.
        return new Result<Output> { Output = new Output(order.Id, order.Number) }
            .Effect(new EventPublished(new OrderCreated(
                order.Id.Value, order.Number.Value, order.Type)));
    }
}

public static class CreateOrderDerivations
{
    [ServerDerivation("orders.create.available-projects")]
    [DependsOn(nameof(CreateOrder.Input.CustomerId), nameof(CreateOrder.Input.OrderType))]
    public static DerivationResult AvailableProjects(
        CreateOrder.Input input, DerivationContext context)
    {
        // Operation-owned conditional requiredness (docs/40): a PROJECT order requires a project.
        // This is the canonical rule of the create-order operation, authoritative at submit for
        // EVERY caller (direct, MCP, integration) with the domain finding orders.project-required —
        // and the indicator forms derive via resolve. It is no longer a form's RequiredWhen (which
        // only tightened whoever happened to submit through that one form).
        var isProject = input.OrderType == OrderType.Project;
        var result = DerivationResult.Empty
            .Require(nameof(CreateOrder.Input.ProjectId), isProject, OrderFindings.ProjectRequired);
        if (!isProject || input.CustomerId.Value == Guid.Empty)
            return result;

        // Authoritative membership (docs/40): the candidate universe is projects.lookup scoped to
        // THIS customer via the view's Filterable customerId. The client OPENS that view (paginated,
        // searchable) scoped by the same base filter — the derivation does NOT materialize the whole
        // candidate set as inline options (Sol re-review, Finding 6). Submit rejects a projectId
        // outside the universe (another customer's, a closed/forged one) by an Exists against the
        // base filter — never by whichever page the client last loaded.
        return result.Lookup(
            nameof(CreateOrder.Input.ProjectId), "projects.lookup",
            new Dictionary<string, string?> { ["customerId"] = input.CustomerId.Value.ToString() },
            OrderFindings.ProjectNotAvailable);
    }

    [ServerDerivation("orders.create.customer-state")]
    [DependsOn(nameof(CreateOrder.Input.CustomerId))]
    public static async Task<DerivationResult> CustomerState(
        CreateOrder.Input input, DerivationContext context, ErpDbContext db, CancellationToken ct)
    {
        if (input.CustomerId.Value == Guid.Empty) return DerivationResult.Empty;

        var check = await OrderRules.CustomerCanReceiveOrder(
            input.CustomerId, context.Operation.TenantId, db, ct);
        if (check.IsError)
            return DerivationResult.From(check, target: nameof(CreateOrder.Input.CustomerId));

        // Same inherited scope as the rule: the picked customer may be ancestor-owned (docs/27).
        var customer = await db.Customers.WithInherited(db, context.Operation.TenantId)
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
[Widens("orders.edit-all")]
[AcceptsExtensions(typeof(Order))]
public static class EditOrderDetails
{
    public sealed record Input(
        OrderId OrderId,
        Change<OrderDescription?>? Description = null,
        Change<DateOnly?>? RequestedDate = null,
        Change<Address?>? WorkAddress = null,
        Change<Money?>? EstimatedTotal = null);

    public sealed record Output(long Version);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == input.OrderId, ct);
        if (order is null) return OrderFindings.NotFound;

        // The write-side twin of the list's scope: base-atom holders edit only their own orders.
        // (The old :own model never checked here — the fail-open the paired-atom pattern closes.)
        var scope = context.CheckOwnershipUnless("orders.edit-all", order.AssignedToActorId);
        if (scope.IsError) return scope.As<Output>();

        if (!order.IsEditable) return OrderFindings.NotEditable;

        var merge = TamMerge.Apply(order, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();

        return new Output(order.Version + 1);
    }
}

[View("orders.list")]
[Authorize("orders.read")]
[Widens("orders.read-all")]
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
        public OrderPriority Priority { get; init; }
        public DateOnly? ScheduledDate { get; init; }
        [LabelKey("labels.assignee")]
        public string? AssignedToName { get; init; }
        public DateOnly? RequestedDate { get; init; }
        public Money? EstimatedTotal { get; init; }
        [LabelKey("labels.company")]
        public string TenantId { get; init; } = "";
        public long Version { get; init; }
        public ExtensionData Extensions { get; init; } = new();
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        // InScope: this query composes a WIDENED source (the customer join below), and EF's
        // IgnoreQueryFilters is query-wide — the orders side must scope itself explicitly (the
        // TamTreeScopes composition rule). InScope = the acting node plus the SubtreeRead set,
        // so this one query serves both the strict and the group-wide request.
        var orders = db.Orders.InScope(db, context.TenantId)
            .ScopedUnless(context, "orders.read-all", x => x.AssignedToActorId);
        if (!string.IsNullOrWhiteSpace(query.Search))
            orders = orders.Where(x =>
                ((string)(object)x.Number).Contains(query.Search!) ||
                ((string)(object)x.Description).Contains(query.Search!));

        // The customer join uses the SAME inherited scope the create validated against (docs/27):
        // an order may reference an ancestor-owned customer, and a strict join would silently drop
        // that order from the list — the widened read and the widened reference must move together.
        return orders
            .Join(db.Customers.WithInherited(db, context.TenantId),
                o => o.CustomerId, c => c.Id, (o, c) => new Result
            {
                Id = o.Id, Number = o.Number, CustomerName = c.Name, Type = o.Type,
                Status = o.Status, Priority = o.Priority, ScheduledDate = o.ScheduledDate,
                AssignedToName = o.AssignedToName, RequestedDate = o.RequestedDate,
                EstimatedTotal = o.EstimatedTotal, TenantId = o.TenantId,
                Version = o.Version, Extensions = o.Extensions,
            });
    }

    // SubtreeRead (docs/26 D-H1): THE orders list is also the group roll-up — standing at a
    // parent shows every descendant company's orders with a mechanical company column + tenant
    // filter; standing at a leaf it behaves exactly as before. The dedicated overview view this
    // replaces is retired (docs/29 ledger).
    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Number), nameof(Result.CustomerName), nameof(Result.RequestedDate))
        .Filterable(nameof(Result.Status), nameof(Result.Type), nameof(Result.Priority),
            nameof(Result.ScheduledDate), nameof(Result.AssignedToName), nameof(Result.CustomerName),
            nameof(Result.RequestedDate), nameof(Result.EstimatedTotal))
        .SubtreeRead(nameof(Result.TenantId))
        .DefaultSort(nameof(Result.Number), descending: true);
}

/// <summary>Detail view backing the edit form: current values + version for the merge base.</summary>
[View("orders.detail")]
[Authorize("orders.read")]
[Widens("orders.read-all")]
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
        public OrderPriority Priority { get; init; }
        public DateOnly? ScheduledDate { get; init; }
        [LabelKey("labels.assignee")]
        public string? AssignedToName { get; init; }
        public Address WorkAddress { get; init; }
        public OrderDescription Description { get; init; }
        public DateOnly? RequestedDate { get; init; }
        public Money? EstimatedTotal { get; init; }
        public long Version { get; init; }
        public ExtensionData Extensions { get; init; } = new();
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context) =>
        // Same inherited customer scope as orders.list; InNode on the orders side for the same
        // query-wide-IgnoreQueryFilters reason (TamTreeScopes composition rule).
        db.Orders.InNode(context.TenantId).Where(x => x.Id == query.OrderId)
            .ScopedUnless(context, "orders.read-all", x => x.AssignedToActorId)
            .Join(db.Customers.WithInherited(db, context.TenantId),
                o => o.CustomerId, c => c.Id, (o, c) => new Result
            {
                Id = o.Id, Number = o.Number, CustomerName = c.Name, Type = o.Type,
                Status = o.Status, Priority = o.Priority, ScheduledDate = o.ScheduledDate,
                AssignedToName = o.AssignedToName, WorkAddress = o.WorkAddress,
                Description = o.Description, RequestedDate = o.RequestedDate,
                EstimatedTotal = o.EstimatedTotal,
                Version = o.Version, Extensions = o.Extensions,
            });
}
