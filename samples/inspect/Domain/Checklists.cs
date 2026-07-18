using Tam.EntityFrameworkCore;

namespace Inspect;

/// <summary>
/// The plugin's own aggregate: stored in the host's database (the host opts in via
/// <c>AddInspect()</c> on its ModelBuilder), audited and stamped by the same pipeline.
/// Inspect v2 (docs/34 M6): a checklist may be INSTANTIATED from a template — it then
/// carries the template's items and the template's mandatory flag. Mandatoriness is
/// template DATA enforced by the plugin's gate on orders.complete; a non-mandatory
/// checklist never blocks anything.
/// </summary>
public sealed class Checklist : ITenantScoped
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Guid? OrderId { get; private set; }
    /// <summary>Set when the checklist was instantiated from a template (the
    /// idempotency key of auto-instantiation: one checklist per (order, template)).</summary>
    public Guid? TemplateId { get; private set; }
    public string Title { get; private set; } = "";
    public bool Mandatory { get; private set; }
    public bool Passed { get; private set; }

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

    // The checklist-level transitions are entity-owned; the CROSS-ENTITY invariant (never
    // passed while items are open) needs the database, so it stays in the operations —
    // the ERP idiom: the entity only protects its own state.
    public void Pass() => Passed = true;

    public void Reopen() => Passed = false;
}

/// <summary>One line on an instantiated checklist. OrderId is denormalized from the owning
/// checklist so the order-detail panel and the completion gate read items by the wire key
/// they hold (the order id) in one indexed query — no joins across the seam.</summary>
public sealed class ChecklistItem : ITenantScoped
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Guid ChecklistId { get; private set; }
    public Guid? OrderId { get; private set; }
    public int Position { get; private set; }
    public string Text { get; private set; } = "";
    public bool Done { get; private set; }

    public static ChecklistItem Create(
        string tenantId, Guid checklistId, Guid? orderId, int position, string text) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ChecklistId = checklistId,
        OrderId = orderId,
        Position = position,
        Text = text,
    };

    public void Check() => Done = true;

    public void Uncheck() => Done = false;
}
