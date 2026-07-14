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

        foreach (var culture in new[] { "sv", "en" })
        {
            using var stream = typeof(InspectionPlugin).Assembly
                .GetManifestResourceStream($"Inspect.locales.{culture}.json");
            if (stream is null) continue;
            plugin.LocaleDefaults(
                culture, JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? []);
        }

        plugin.Model.Form<CreateChecklist.Input>(
            "inspect.web.create", "inspect.checklists.create", form =>
        {
            form.Field(x => x.Title);
        });

        plugin.Model.Grid<ChecklistList.Result>(
            "inspect.web.checklists", "inspect.checklists.list", grid =>
        {
            grid.Column(x => x.Title);
            grid.Column(x => x.Passed);
            grid.RowAction("inspect.checklists.pass");
            grid.ToolbarAction("inspect.checklists.create");
        });

        // P2 — a packaged field on the HOST's entity: rides the same extension channel as
        // tenant custom fields (forms, grids, audit, MCP, D7 filters come free), key-prefixed
        // and present only where the plugin is active. Label lives in the locale files above.
        plugin.ExtensionField("order", "requiresInspection", "boolean");

        // P2 — a gate on the HOST's operation: an order with an unpassed linked checklist
        // cannot complete. The gate reads the wire input and the plugin's OWN data — never
        // host CLR types. Visible in the manifest as orders.complete.gatedBy: ["inspect"].
        plugin.Gate("orders.complete", async (gate, ct) =>
        {
            if (!gate.Input.TryGetProperty("orderId", out var idElement)
                || !idElement.TryGetGuid(out var orderId))
                return Result.Success();

            var db = ((ITamDb)gate.Services.GetService(typeof(ITamDb))!).Db;
            var blocked = await db.Set<Checklist>().AnyAsync(
                x => x.TenantId == gate.Context.TenantId.Value && x.OrderId == orderId && !x.Passed, ct);
            return blocked ? InspectFindings.ChecklistIncomplete : Result.Success();
        });

        // P2 — an effect subscriber: when the host commits an order completion, open a
        // follow-up checklist. Post-commit via the outbox, in the plugin's own operation
        // scope — never by patching the host handler.
        plugin.OnEffect("order-completed", async (effect, services, ct) =>
        {
            if (!effect.Payload.TryGetProperty("number", out var numberElement)
                || numberElement.GetString() is not { Length: > 0 } number)
                return;
            var orderId = effect.Payload.TryGetProperty("orderId", out var idElement)
                && idElement.TryGetGuid(out var id) ? id : (Guid?)null;

            var db = ((ITamDb)services.GetService(typeof(ITamDb))!).Db;
            // Outbox delivery is at-least-once — the subscriber must be idempotent.
            var exists = orderId is { } linked && await db.Set<Checklist>().AnyAsync(
                x => x.TenantId == effect.TenantId && x.OrderId == linked && x.Title == number, ct);
            if (exists) return;
            db.Add(Checklist.Create(effect.TenantId, number, orderId));
            await db.SaveChangesAsync(ct);
        });
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
