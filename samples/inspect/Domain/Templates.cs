using Tam.EntityFrameworkCore;

namespace Inspect;

/// <summary>
/// A tenant-defined checklist TEMPLATE keyed on the host's order type (a WIRE value —
/// "service", "project" — never a host CLR type; the plugin composes around the host's
/// contract). Retire-don't-delete: instantiated checklists outlive their template.
/// </summary>
public sealed class ChecklistTemplate : ITenantScoped
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>The host order type this template matches, as its wire value.</summary>
    public string OrderType { get; private set; } = "";
    public bool Mandatory { get; private set; }
    public bool Retired { get; private set; }

    public static ChecklistTemplate Create(
        string tenantId, string name, string orderType, bool mandatory) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = name,
        OrderType = orderType.Trim().ToLowerInvariant(),
        Mandatory = mandatory,
    };

    /// <summary>Retire, never delete: stops instantiating; existing checklists live on.</summary>
    public void Retire() => Retired = true;
}

/// <summary>A line on a template, copied onto every checklist instantiated from it.</summary>
public sealed class ChecklistTemplateItem : ITenantScoped
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Guid TemplateId { get; private set; }
    public int Position { get; private set; }
    public string Text { get; private set; } = "";

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
