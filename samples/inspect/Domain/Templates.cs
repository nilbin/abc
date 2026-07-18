using Tam;
using Tam.EntityFrameworkCore;

namespace Inspect;

public static class TemplateFindings
{
    public static readonly FindingFactory TemplateRetired =
        Finding.Error("inspect.template-retired");
    public static readonly FindingFactory ItemTextRequired =
        Finding.Error("inspect.item-text-required");
    public static readonly FindingFactory NameRequired =
        Finding.Error("inspect.name-required");
    public static readonly FindingFactory OrderTypeRequired =
        Finding.Error("inspect.order-type-required");
}


/// <summary>
/// A tenant-defined checklist TEMPLATE keyed on the host's order type (a WIRE value —
/// "service", "project" — never a host CLR type; the plugin composes around the host's
/// contract). Retire-don't-delete: instantiated checklists outlive their template.
/// The template is the aggregate ROOT of its lines: they enter only through
/// <see cref="AddItem"/> (which owns the position sequence and the retired guard) and are
/// copied onto checklists only through <see cref="Instantiate"/> — load with Items.
/// </summary>
public sealed class ChecklistTemplate : ITenantScoped
{
    private readonly List<ChecklistTemplateItem> items = [];

    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>The host order type this template matches, as its wire value.</summary>
    public string OrderType { get; private set; } = "";
    public bool Mandatory { get; private set; }
    public bool Retired { get; private set; }
    public IReadOnlyList<ChecklistTemplateItem> Items => items;

    public static ChecklistTemplate Create(
        string tenantId, string name, string orderType, bool mandatory) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = name,
        OrderType = orderType.Trim().ToLowerInvariant(),
        Mandatory = mandatory,
    };

    /// <summary>Append a line. The root owns the 1-based position sequence — contiguous by
    /// construction — and a retired template refuses to grow.</summary>
    public Result<ChecklistTemplateItem> AddItem(string text)
    {
        if (Retired) return TemplateFindings.TemplateRetired.Create();
        if (string.IsNullOrWhiteSpace(text)) return TemplateFindings.ItemTextRequired.Create();
        var item = ChecklistTemplateItem.Create(TenantId, Id, items.Count + 1, text.Trim());
        items.Add(item);
        return item;
    }

    /// <summary>The template is the checklist FACTORY: one call stamps the title, the
    /// mandatory flag, and every line onto a new checklist for the order. A retired
    /// template refuses (callers filter; the aggregate keeps them honest).</summary>
    public Result<Checklist> Instantiate(Guid orderId, string orderNumber)
    {
        if (Retired) return TemplateFindings.TemplateRetired.Create();
        var title = orderNumber.Length > 0 ? $"{Name} — {orderNumber}" : Name;
        var checklist = Checklist.Create(TenantId, title, orderId, Mandatory, Id);
        foreach (var item in items.OrderBy(x => x.Position))
            checklist.CarryLine(item.Position, item.Text);
        return checklist;
    }

    /// <summary>Retire, never delete: stops instantiating; existing checklists live on.</summary>
    public void Retire() => Retired = true;
}

/// <summary>A line on a template. No identity outside its template: construction and
/// position assignment belong to the root's <see cref="ChecklistTemplate.AddItem"/>.</summary>
public sealed class ChecklistTemplateItem : ITenantScoped
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "";
    public Guid TemplateId { get; private set; }
    public int Position { get; private set; }
    public string Text { get; private set; } = "";

    internal static ChecklistTemplateItem Create(
        string tenantId, Guid templateId, int position, string text) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TemplateId = templateId,
        Position = position,
        Text = text,
    };
}
