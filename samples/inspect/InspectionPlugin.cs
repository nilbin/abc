using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam;
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
