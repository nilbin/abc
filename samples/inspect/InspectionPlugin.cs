using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;
using Tam.Generated;

namespace Inspect;

/// <summary>
/// Inspect v2 (docs/34 M6): the tutorial's step-13 plugin grown into a real feature —
/// tenant-defined checklist TEMPLATES keyed on order type, auto-instantiated onto new
/// orders, per-item check-off intents, and a completion gate that blocks orders.complete
/// while a MANDATORY checklist has open items. The host still adds one line
/// (<c>AddPlugin&lt;InspectionPlugin&gt;()</c>) plus the storage opt-in; activation stays
/// per tenant. Configure is a table of contents over cohesive PARTS (the invoicing shape).
/// </summary>
[TamPlugin("inspect")]
public sealed class InspectionPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.AddDiscovered();   // operations, views, [Gate]/[OnEffect] behaviors
        plugin.AddPart<OrdersContract>();     // everything host-facing
        plugin.AddPart<TemplateAdminSurface>();   // the tenant admin's template UI
        plugin.AddPart<ChecklistSurface>();       // the checklist UI (page + order panel)
    }

    /// <summary>Host opt-in for the plugin's storage: one line in the host's OnModelCreating.
    /// The plugin's tables live in the host database and migrate with it (docs/22).</summary>
    public static ModelBuilder AddInspect(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Checklist>(b =>
        {
            b.ToTable("inspect_checklists");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.Passed });
            // The gate's hot read: mandatory-open checklists for (tenant, order).
            b.HasIndex(x => new { x.TenantId, x.OrderId });
        });
        modelBuilder.Entity<ChecklistItem>(b =>
        {
            b.ToTable("inspect_checklist_items");
            b.HasKey(x => x.Id);
            b.Property(x => x.Text).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.ChecklistId });
            b.HasIndex(x => new { x.TenantId, x.OrderId });
        });
        modelBuilder.Entity<ChecklistTemplate>(b =>
        {
            b.ToTable("inspect_templates");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.OrderType).HasMaxLength(50);
            // The subscriber's hot read: active templates for (tenant, order type).
            b.HasIndex(x => new { x.TenantId, x.OrderType });
        });
        modelBuilder.Entity<ChecklistTemplateItem>(b =>
        {
            b.ToTable("inspect_template_items");
            b.HasKey(x => x.Id);
            b.Property(x => x.Text).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.TemplateId });
        });
        return modelBuilder;
    }
}

/// <summary>
/// The host-facing contract in one place (the docs/31 D-X5 shape): the packaged field the
/// plugin puts on orders, the events it consumes, the event it publishes, and where its
/// panels land on host surfaces. This part IS the install screen's story.
/// </summary>
internal sealed class OrdersContract : IPluginPart
{
    public void Configure(PluginBuilder plugin)
    {
        // P2 — a packaged field on the HOST's entity, key-prefixed, present only where the
        // plugin is active. Label lives in the plugin's locale files.
        plugin.ExtensionField("order", "requiresInspection", "boolean");

        // Event contracts (PLG009): payload shapes are declared, never folklore.
        // order-created is the v2 seam — matching templates instantiate onto the new order.
        plugin.RequiresEvent("order-created", "orderId", "number", "orderType");
        plugin.RequiresEvent("order-completed", "orderId", "number");
        plugin.PublishesEvent("inspect.checklist-passed", "checklistId");

        // The order detail wears its checklists (docs/31 D-X4): two panels bound to the
        // slot's record context — the checklist headers, then the line items with the
        // check/uncheck row actions. The host opted the surface in once; it never names us.
        plugin.Panel("web.orders.detail", grid: "inspect.web.checklists",
            bind => bind.Query("orderId", fromContext: "orderId"));
        plugin.Panel("web.orders.detail", grid: "inspect.web.items",
            bind => bind.Query("orderId", fromContext: "orderId"));
    }
}

/// <summary>The tenant admin's surface: define templates, add lines, retire — a declared
/// page (grid + the template-lines grid), suggested into the host's administration area.</summary>
internal sealed class TemplateAdminSurface : IPluginPart
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.Form<DefineTemplate.Input>(
            "inspect.web.templates.define", "inspect.templates.define", form =>
        {
            form.Field(x => x.Name);
            // The HOST's order-type vocabulary as options, without referencing its CLR enum
            // (docs/34 M6): a typo can no longer define a template that never fires.
            form.Field(x => x.OrderType).EnumOptions("order-type");
            form.Field(x => x.Mandatory);
        });

        // Reached as a row action on the templates grid — the template arrives prefilled.
        plugin.Form<AddTemplateItem.Input>(
            "inspect.web.templates.add-item", "inspect.templates.add-item", form =>
        {
            form.Field(x => x.TemplateId).Renderer("hidden");
            form.Field(x => x.Text);
        });

        plugin.Grid<TemplateList.Result>(
            "inspect.web.templates", "inspect.templates.list", grid =>
        {
            grid.RowAction("inspect.templates.add-item");
            grid.RowAction("inspect.templates.retire");
            grid.ToolbarAction("inspect.templates.define");
        });

        plugin.Grid<TemplateItemList.Result>(
            "inspect.web.template-items", "inspect.templates.items");

        // Templates, then every line they will stamp — ordered sections (docs/32 D-P4),
        // labeled so the two grids read as two surfaces (docs/34 M6 headings).
        plugin.Page("inspect.templates", page => page
            .Grid("inspect.web.templates", heading: "inspect.headings.templates")
            .Grid("inspect.web.template-items", heading: "inspect.headings.template-items"));

        plugin.Nav(nav => nav.Page("inspect.templates",
            page: "inspect.templates", suggest: "administration", order: 60));
    }
}

/// <summary>The checklist work surface: the plugin's own page (checklists + items), kept
/// beside the order-detail panels the OrdersContract part contributes.</summary>
internal sealed class ChecklistSurface : IPluginPart
{
    public void Configure(PluginBuilder plugin)
    {
        // No configure: the record IS the form (docs/32). Title, OrderId, Mandatory.
        plugin.Form<CreateChecklist.Input>(
            "inspect.web.create", "inspect.checklists.create");

        // Columns default to the result record (docs/32); only the ACTIONS are a decision.
        plugin.Grid<ChecklistList.Result>(
            "inspect.web.checklists", "inspect.checklists.list", grid =>
        {
            grid.RowAction("inspect.checklists.pass");
            grid.ToolbarAction("inspect.checklists.create");
        });

        plugin.Grid<ChecklistItemList.Result>(
            "inspect.web.items", "inspect.items.list", grid =>
        {
            grid.RowAction("inspect.items.check");
            grid.RowAction("inspect.items.uncheck");
        });

        plugin.Page("inspect.checklists", page => page
            .Grid("inspect.web.checklists")
            .Grid("inspect.web.items"));

        plugin.Nav(nav => nav.Page("inspect.checklists",
            page: "inspect.checklists", suggest: "work", order: 50));
    }
}

/// <summary>An order with a MANDATORY unpassed checklist cannot complete — the gate reads
/// the wire input and the plugin's OWN data, never host CLR types (docs/22 P2).
/// Mandatoriness is template data the checklist carries; non-mandatory checklists never
/// block (docs/34 M6 — deliberately NOT a tenant automation rule: v1 rule conditions see
/// only the input, and orders.complete carries just an id).</summary>
[Gate("orders.complete")]
internal sealed class ChecklistGate(ITamDb tam) : IOperationGate
{
    public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
    {
        if (gate.Guid("orderId") is not { } orderId) return Result.Success();

        var blocking = await tam.Db.Set<Checklist>()
            .Where(x => x.OrderId == orderId && x.Mandatory && !x.Passed)
            .Select(x => new
            {
                x.Title,
                Open = tam.Db.Set<ChecklistItem>().Count(i => i.ChecklistId == x.Id && !i.Done),
            })
            .FirstOrDefaultAsync(ct);
        return blocking is null
            ? Result.Success()
            : InspectFindings.ChecklistIncomplete.With(
                ("title", blocking.Title), ("open", blocking.Open));
    }
}

/// <summary>Inspect v2's core seam: when the host commits an order creation, every ACTIVE
/// template matching the order's type instantiates as a checklist (with its items) attached
/// to that order — post-commit via the outbox, tenant-pinned, idempotent per
/// (order, template) since delivery is at-least-once.</summary>
[OnEffect("order-created")]
internal sealed class InstantiateTemplates(ITamDb tam) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        if (effect.Guid("orderId") is not { } orderId) return;
        var orderType = effect.String("orderType") ?? "";
        if (orderType.Length == 0) return;
        var number = effect.String("number") ?? "";

        var normalized = orderType.Trim().ToLowerInvariant();
        var templates = await tam.Db.Set<ChecklistTemplate>()
            .Where(x => !x.Retired && x.OrderType == normalized)
            .ToListAsync(ct);
        if (templates.Count == 0) return;

        var changed = false;
        foreach (var template in templates)
        {
            // At-least-once delivery: one checklist per (order, template), ever.
            if (await tam.Db.Set<Checklist>().AnyAsync(
                    x => x.OrderId == orderId && x.TemplateId == template.Id, ct))
                continue;

            var title = number.Length > 0 ? $"{template.Name} — {number}" : template.Name;
            var checklist = Checklist.Create(
                effect.TenantId, title, orderId, template.Mandatory, template.Id);
            tam.Db.Add(checklist);
            var items = await tam.Db.Set<ChecklistTemplateItem>()
                .Where(x => x.TemplateId == template.Id)
                .OrderBy(x => x.Position)
                .ToListAsync(ct);
            foreach (var item in items)
                tam.Db.Add(ChecklistItem.Create(
                    effect.TenantId, checklist.Id, orderId, item.Position, item.Text));
            changed = true;
        }
        if (changed) await tam.Db.SaveChangesAsync(ct);
    }
}

/// <summary>v1 behavior, kept: when the host commits an order completion, open a follow-up
/// checklist — post-commit via the outbox, tenant-pinned, idempotent.</summary>
[OnEffect("order-completed")]
internal sealed class OpenFollowUpChecklist(ITamDb tam) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        if (effect.String("number") is not { Length: > 0 } number) return;
        var orderId = effect.Guid("orderId");

        // Outbox delivery is at-least-once — the handler must be idempotent. The delivery
        // scope is pinned to the record's tenant, so the ambient filter applies here too.
        var exists = orderId is { } linked && await tam.Db.Set<Checklist>().AnyAsync(
            x => x.OrderId == linked && x.Title == number, ct);
        if (exists) return;
        tam.Db.Add(Checklist.Create(effect.TenantId, number, orderId));
        await tam.Db.SaveChangesAsync(ct);
    }
}
