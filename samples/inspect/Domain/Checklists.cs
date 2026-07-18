using Tam;
using Tam.EntityFrameworkCore;

namespace Inspect;

public static class ChecklistFindings
{
    public static readonly FindingFactory ChecklistIncomplete =
        Finding.Error("inspect.checklist-incomplete");
    public static readonly FindingFactory ItemsOpen =
        Finding.Error("inspect.items-open");
}


/// <summary>
/// The plugin's own aggregate: stored in the host's database (the host opts in via
/// <c>AddInspect()</c> on its ModelBuilder), audited and stamped by the same pipeline.
/// Inspect v2 (docs/34 M6): a checklist may be INSTANTIATED from a template — it then
/// carries the template's items and the template's mandatory flag. Mandatoriness is
/// template DATA enforced by the plugin's gate on orders.complete; a non-mandatory
/// checklist never blocks anything.
/// The checklist is the aggregate ROOT of its lines (load with Items): checking, unchecking
/// and passing go through the root, so the item states and the checklist state move in ONE
/// transition and can never disagree.
/// </summary>
public sealed class Checklist : ITenantScoped
{
    private readonly List<ChecklistItem> items = [];

    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Guid? OrderId { get; private set; }
    /// <summary>Set when the checklist was instantiated from a template (the
    /// idempotency key of auto-instantiation: one checklist per (order, template)).</summary>
    public Guid? TemplateId { get; private set; }
    public string Title { get; private set; } = "";
    public bool Mandatory { get; private set; }
    public bool Passed { get; private set; }
    public IReadOnlyList<ChecklistItem> Items => items;

    public static Checklist Create(
        string tenantId, string title, Guid? orderId,
        bool mandatory = false, Guid? templateId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Title = title,
        OrderId = orderId,
        Mandatory = mandatory,
        TemplateId = templateId,
    };

    /// <summary>Instantiation-time copy — only the owning template calls this
    /// (<see cref="ChecklistTemplate.Instantiate"/>).</summary>
    internal void CarryLine(int position, string text) =>
        items.Add(ChecklistItem.Create(TenantId, Id, OrderId, position, text));

    /// <summary>Passing is the checklist-level close, and the aggregate REFUSES it while
    /// its own lines are open — the item state and the checklist state can never disagree.
    /// Item-less (manual) checklists pass directly: this is their only completion path.</summary>
    public Result Pass()
    {
        var open = items.Count(x => !x.Done);
        if (open > 0) return ChecklistFindings.ItemsOpen.With(("open", open));
        Passed = true;
        return Result.Success();
    }

    /// <summary>Check one line off. When the last open line closes, the checklist passes as
    /// part of the SAME transition — one intent, one consistent state. Returns whether the
    /// checklist is now passed.</summary>
    public Result<bool> Check(Guid itemId)
    {
        if (items.SingleOrDefault(x => x.Id == itemId) is not { } item)
            return PipelineFindings.NotFound.Create();
        item.Check();
        var passed = items.All(x => x.Done);
        if (passed) Passed = true;
        return passed;
    }

    /// <summary>The correction path: un-checking a line re-opens the checklist in the same
    /// transition — a passed checklist with an open line is unrepresentable (why this
    /// exists rather than a pass-only model: inspections get amended).</summary>
    public Result Uncheck(Guid itemId)
    {
        if (items.SingleOrDefault(x => x.Id == itemId) is not { } item)
            return PipelineFindings.NotFound.Create();
        item.Uncheck();
        Passed = false;
        return Result.Success();
    }
}

/// <summary>One line on an instantiated checklist. OrderId is denormalized from the owning
/// checklist so the order-detail panel and the completion gate read items by the wire key
/// they hold (the order id) in one indexed query — no joins across the seam. State moves
/// only through the root (<see cref="Checklist.Check"/>/<see cref="Checklist.Uncheck"/>).</summary>
public sealed class ChecklistItem : ITenantScoped
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Guid ChecklistId { get; private set; }
    public Guid? OrderId { get; private set; }
    public int Position { get; private set; }
    public string Text { get; private set; } = "";
    public bool Done { get; private set; }

    internal static ChecklistItem Create(
        string tenantId, Guid checklistId, Guid? orderId, int position, string text) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ChecklistId = checklistId,
        OrderId = orderId,
        Position = position,
        Text = text,
    };

    internal void Check() => Done = true;

    internal void Uncheck() => Done = false;
}
