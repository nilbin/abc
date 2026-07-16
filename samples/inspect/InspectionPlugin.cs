using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;
using Tam.Generated;

namespace Inspect;

/// <summary>
/// The tutorial's step-13 plugin (docs/22): inspection checklists as a compiled, namespaced
/// module. The host adds one line — <c>AddPlugin&lt;InspectionPlugin&gt;()</c> — and activates
/// per tenant at runtime; everything registered here is tagged "inspect" and omitted from the
/// manifest for tenants that haven't.
/// </summary>
[TamPlugin("inspect")]
public sealed class InspectionPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // The plugin's own compile-time discovery — the generated class is internal per
        // assembly, so host and plugins never collide.
        plugin.Model.AddDiscovered();

        // Embedded locales/*.json by convention; application locale files override.
        plugin.LocaleDefaults();

        // No configure: the record IS the form (docs/32). Title + OrderId, declaration order.
        plugin.Form<CreateChecklist.Input>(
            "inspect.web.create", "inspect.checklists.create");

        // Columns default to the result record (docs/32); only the ACTIONS are a decision.
        plugin.Grid<ChecklistList.Result>(
            "inspect.web.checklists", "inspect.checklists.list", grid =>
        {
            grid.RowAction("inspect.checklists.pass");
            grid.ToolbarAction("inspect.checklists.create");
        });

        // P2 — a packaged field on the HOST's entity: rides the same extension channel as
        // tenant custom fields (forms, grids, audit, MCP, D7 filters come free), key-prefixed
        // and present only where the plugin is active. Label lives in the locale files above.
        plugin.ExtensionField("order", "requiresInspection", "boolean");

        // P2 — the gate and subscriber register from their OWN attributes ([Gate]/[OnEffect]
        // below, picked up by AddDiscovered like [Operation]/[View]): declaration lives on the
        // behavior. Only the payload CONTRACT is declared here (PLG009).
        plugin.RequiresEvent("order-completed", "orderId", "number");
        plugin.PublishesEvent("inspect.checklist-passed", "checklistId");
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
        });
        return modelBuilder;
    }
}

/// <summary>An order with an unpassed linked checklist cannot complete — the gate reads the
/// wire input and the plugin's OWN data, never host CLR types (docs/22 P2). Registration is
/// the attribute; the manifest still shows orders.complete.gatedBy: ["inspect"].</summary>
[Gate("orders.complete")]
internal sealed class ChecklistGate(ITamDb tam) : IOperationGate
{
    public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
    {
        if (!gate.Input.TryGetProperty("orderId", out var idElement)
            || !idElement.TryGetGuid(out var orderId))
            return Result.Success();

        var blocked = await tam.Db.Set<Checklist>().AnyAsync(
            x => x.OrderId == orderId && !x.Passed, ct);
        return blocked ? InspectFindings.ChecklistIncomplete : Result.Success();
    }
}

/// <summary>When the host commits an order completion, open a follow-up checklist — post-
/// commit via the outbox, tenant-pinned, idempotent (at-least-once delivery).</summary>
[OnEffect("order-completed")]
internal sealed class OpenFollowUpChecklist(ITamDb tam) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        if (!effect.Payload.TryGetProperty("number", out var numberElement)
            || numberElement.GetString() is not { Length: > 0 } number)
            return;
        var orderId = effect.Payload.TryGetProperty("orderId", out var idElement)
            && idElement.TryGetGuid(out var id) ? id : (Guid?)null;

        // Outbox delivery is at-least-once — the handler must be idempotent. The delivery
        // scope is pinned to the record's tenant, so the ambient filter applies here too.
        var exists = orderId is { } linked && await tam.Db.Set<Checklist>().AnyAsync(
            x => x.OrderId == linked && x.Title == number, ct);
        if (exists) return;
        tam.Db.Add(Checklist.Create(effect.TenantId, number, orderId));
        await tam.Db.SaveChangesAsync(ct);
    }
}
