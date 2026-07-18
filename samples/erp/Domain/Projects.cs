using Tam;

namespace Erp;

// The Projects aggregate: value types, finding factories, entity (docs/02).


[LabelKey("labels.project"), Lookup("projects.lookup")]
public readonly record struct ProjectId(Guid Value);


// labels.number is already claimed by orders ("Order number") — the flat label namespace
// makes same-named members collide across aggregates, so this type carries its own key
// (docs/34 friction log).
[LabelKey("labels.project-number")]
public readonly record struct ProjectNumber(string Value);


public enum ProjectStatus { Open, Closed }


public static class ProjectFindings
{
    public static readonly FindingFactory NotFound = Finding.Error("projects.not-found");
    public static readonly FindingFactory DuplicateNumber = Finding.Error("projects.duplicate-number");
    public static readonly FindingFactory NotOpen = Finding.Error("projects.not-open");
    public static readonly FindingFactory NotClosed = Finding.Error("projects.not-closed");
    public static readonly FindingFactory HasOpenOrders = Finding.Error("projects.has-open-orders");
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
