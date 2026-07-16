using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

[Operation("projects.create")]
[Authorize("projects.create")]
public static class CreateProject
{
    public sealed record Input(
        [property: LabelKey("labels.customer")] CustomerId CustomerId,
        ProjectNumber Number,
        string Name,
        Money? Budget = null);

    public sealed record Output(ProjectId ProjectId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        // Same reference rule as orders: a project may hang off an ancestor-owned customer.
        var customerCheck = await OrderRules.CustomerCanReceiveOrder(
            input.CustomerId, context.TenantId, db, ct);
        if (customerCheck.IsError) return customerCheck.As<Output>();

        var exists = await db.Projects.AnyAsync(x => x.Number == input.Number, ct);
        if (exists)
            return ProjectFindings.DuplicateNumber.At(nameof(Input.Number));

        var project = Project.Create(
            context.TenantId.Value, input.Number, input.CustomerId, input.Name, input.Budget);
        db.Projects.Add(project);
        return new Output(project.Id);
    }
}

[Operation("projects.edit-details")]
[Authorize("projects.edit")]
public static class EditProjectDetails
{
    public sealed record Input(
        [property: LabelKey("labels.project")] ProjectId ProjectId,
        Change<string>? Name = null,
        Change<Money?>? Budget = null);

    public sealed record Output(ProjectId ProjectId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == input.ProjectId, ct);
        if (project is null) return ProjectFindings.NotFound.Create();
        if (project.Status != ProjectStatus.Open) return ProjectFindings.NotOpen.Create();

        var merge = TamMerge.Apply(project, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();

        return new Output(project.Id);
    }
}

/// <summary>Closing is an INTENT, not an edit (EDIT001): it carries a cross-aggregate guard
/// — a project with open orders cannot close.</summary>
[Operation("projects.close")]
[Authorize("projects.close")]
public static class CloseProject
{
    public sealed record Input([property: LabelKey("labels.project")] ProjectId ProjectId);

    public sealed record Output(ProjectStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == input.ProjectId, ct);
        if (project is null) return ProjectFindings.NotFound.Create();

        var hasOpenOrders = await db.Orders.AnyAsync(
            x => x.ProjectId == input.ProjectId && x.Status == OrderStatus.Open, ct);
        if (hasOpenOrders) return ProjectFindings.HasOpenOrders.Create();

        var result = project.Close();
        if (result.IsError) return result.As<Output>();
        return new Output(project.Status);
    }
}

[Operation("projects.reopen")]
[Authorize("projects.close")]
public static class ReopenProject
{
    public sealed record Input([property: LabelKey("labels.project")] ProjectId ProjectId);

    public sealed record Output(ProjectStatus Status);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == input.ProjectId, ct);
        if (project is null) return ProjectFindings.NotFound.Create();

        var result = project.Reopen();
        if (result.IsError) return result.As<Output>();
        return new Output(project.Status);
    }
}

[View("projects.list")]
[Authorize("projects.read")]
public static class ProjectList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public ProjectId Id { get; init; }
        public ProjectNumber Number { get; init; }
        public string Name { get; init; } = "";
        [LabelKey("labels.customer")]
        public CustomerName CustomerName { get; init; }
        public ProjectStatus Status { get; init; }
        public Money? Budget { get; init; }
        [LabelKey("labels.company")]
        public string TenantId { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        // InScope + inherited customer join: the same composition rule as orders.list — the
        // widened project read and the widened customer reference must move together.
        var projects = db.Projects.InScope(db, context.TenantId);
        if (!string.IsNullOrWhiteSpace(query.Search))
            projects = projects.Where(x =>
                x.Name.Contains(query.Search!) ||
                ((string)(object)x.Number).Contains(query.Search!));

        return projects
            .Join(db.Customers.WithInherited(db, context.TenantId),
                p => p.CustomerId, c => c.Id, (p, c) => new Result
            {
                Id = p.Id, Number = p.Number, Name = p.Name, CustomerName = c.Name,
                Status = p.Status, Budget = p.Budget, TenantId = p.TenantId,
            });
    }

    // The projects list is also the group roll-up (docs/26 D-H1): standing at a parent shows
    // every descendant company's projects with the mechanical company column + tenant filter.
    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Number), nameof(Result.Name), nameof(Result.CustomerName))
        .Filterable(nameof(Result.Status), nameof(Result.CustomerName), nameof(Result.Budget))
        .SubtreeRead(nameof(Result.TenantId))
        .DefaultSort(nameof(Result.Number), descending: true);
}

/// <summary>The picker behind every ProjectId field ([Lookup], docs/34 M5): open projects,
/// searched by number or name.</summary>
[View("projects.lookup")]
[Authorize("projects.read")]
public static class ProjectLookup
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public ProjectId Id { get; init; }
        public string Name { get; init; } = "";
        public ProjectNumber Number { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context)
    {
        var projects = db.Projects.Where(x => x.Status == ProjectStatus.Open);
        if (!string.IsNullOrWhiteSpace(query.Search))
            projects = projects.Where(x =>
                x.Name.Contains(query.Search!) ||
                ((string)(object)x.Number).Contains(query.Search!));
        return projects.Select(x => new Result { Id = x.Id, Name = x.Name, Number = x.Number });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Name)).DefaultSort(nameof(Result.Name));
}

/// <summary>The record surface behind the declared projects page (docs/32): fields named to
/// prefill the edit form.</summary>
[View("projects.detail")]
[Authorize("projects.read")]
public static class ProjectDetail
{
    public sealed record Query(ProjectId ProjectId);

    public sealed record Result
    {
        public ProjectId Id { get; init; }
        public ProjectNumber Number { get; init; }
        public string Name { get; init; } = "";
        [LabelKey("labels.customer")]
        public CustomerName CustomerName { get; init; }
        public ProjectStatus Status { get; init; }
        public Money? Budget { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db, OperationContext context) =>
        db.Projects.InNode(context.TenantId).Where(x => x.Id == query.ProjectId)
            .Join(db.Customers.WithInherited(db, context.TenantId),
                p => p.CustomerId, c => c.Id, (p, c) => new Result
            {
                Id = p.Id, Number = p.Number, Name = p.Name, CustomerName = c.Name,
                Status = p.Status, Budget = p.Budget,
            });
}
