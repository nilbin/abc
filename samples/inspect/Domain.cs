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
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid? OrderId { get; set; }
    /// <summary>Set when the checklist was instantiated from a template (the
    /// idempotency key of auto-instantiation: one checklist per (order, template)).</summary>
    public Guid? TemplateId { get; set; }
    public string Title { get; set; } = "";
    public bool Mandatory { get; set; }
    public bool Passed { get; set; }

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
}

/// <summary>One line on an instantiated checklist. OrderId is denormalized from the owning
/// checklist so the order-detail panel and the completion gate read items by the wire key
/// they hold (the order id) in one indexed query — no joins across the seam.</summary>
public sealed class ChecklistItem : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid ChecklistId { get; set; }
    public Guid? OrderId { get; set; }
    public int Position { get; set; }
    public string Text { get; set; } = "";
    public bool Done { get; set; }

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
}

/// <summary>
/// A tenant-defined checklist TEMPLATE keyed on the host's order type (a WIRE value —
/// "service", "project" — never a host CLR type; the plugin composes around the host's
/// contract). Retire-don't-delete: instantiated checklists outlive their template.
/// </summary>
public sealed class ChecklistTemplate : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The host order type this template matches, as its wire value.</summary>
    public string OrderType { get; set; } = "";
    public bool Mandatory { get; set; }
    public bool Retired { get; set; }

    public static ChecklistTemplate Create(
        string tenantId, string name, string orderType, bool mandatory) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = name,
        OrderType = orderType.Trim().ToLowerInvariant(),
        Mandatory = mandatory,
    };
}

/// <summary>A line on a template, copied onto every checklist instantiated from it.</summary>
public sealed class ChecklistTemplateItem : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid TemplateId { get; set; }
    public int Position { get; set; }
    public string Text { get; set; } = "";

    public static ChecklistTemplateItem Create(
        string tenantId, Guid templateId, int position, string text) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TemplateId = templateId,
        Position = position,
        Text = text,
    };
}
