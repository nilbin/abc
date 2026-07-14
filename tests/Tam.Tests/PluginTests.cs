using Tam;

namespace Tam.Tests;

public class PluginTests
{
    [TamPlugin("demo")]
    private sealed class WellBehavedPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.demo.title"] = "Demo",
                ["operations.demo.things.create.title"] = "Create thing",
                ["labels.name"] = "Name",
            });
            plugin.Model.AddOperationType(typeof(CreateThing));
        }
    }

    [Operation("demo.things.create")]
    [Authorize("demo.things.manage")]
    private static class CreateThing
    {
        public sealed record Input(string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    [TamPlugin("rogue")]
    private sealed class EscapingPlugin : ITamPlugin
    {
        // Registers an operation whose id is NOT under "rogue." — must fail PLG001.
        public void Configure(PluginBuilder plugin) =>
            plugin.Model.AddOperationType(typeof(CreateThing));
    }

    [Fact]
    public void Plugin_contributions_are_tagged_and_listed()
    {
        var model = new TamModelBuilder()
            .AddPlugin<WellBehavedPlugin>()
            .Build();

        Assert.Equal("demo", model.Operations["demo.things.create"].Plugin);
        Assert.True(model.Plugins.ContainsKey("demo"));
    }

    [Fact]
    public void PLG001_rejects_contributions_outside_the_plugin_namespace()
    {
        var builder = new TamModelBuilder().AddPlugin<EscapingPlugin>();
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.StartsWith("PLG001", error.Message);
    }

    [TamPlugin("pkg")]
    private sealed class PackagingPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.pkg.title"] = "Pkg",
                ["ext.pkg.priority"] = "Priority",
            });
            plugin.ExtensionField("thing", "priority", "integer");
            plugin.Gate("host.things.close", (_, _) => Task.FromResult(Result.Success()));
        }
    }

    private sealed class Thing : IExtensible
    {
        public ExtensionData Extensions { get; set; } = new();
    }

    [Operation("host.things.close")]
    [Authorize("host.things.close")]
    [AcceptsExtensions(typeof(Thing))]
    private static class CloseThing
    {
        public sealed record Input([property: LabelKey("labels.name")] string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public void Packaged_fields_and_gates_land_in_model_and_manifest()
    {
        var model = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["operations.host.things.close.title"] = "Close",
                ["labels.name"] = "Name",
            })
            .AddOperationType(typeof(CloseThing))
            .AddPlugin<PackagingPlugin>()
            .Build();

        var field = Assert.Single(model.PackagedFields);
        Assert.Equal("pkg.priority", field.Spec.Key);          // key-prefixed, label from locales
        Assert.Equal("Priority", field.Spec.Labels["en"]);
        Assert.Single(model.Gates["host.things.close"]);

        var manifest = ManifestBuilder.Build(model,
            new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0, new HashSet<string> { "pkg" });
        Assert.Equal(["pkg"], manifest.Operations["host.things.close"].GatedBy);

        var inactive = ManifestBuilder.Build(model,
            new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0, new HashSet<string>());
        Assert.Empty(inactive.Operations["host.things.close"].GatedBy);
    }

    [Fact]
    public void Plugin_contributions_referencing_unknown_host_surface_fail_the_build()
    {
        // Without the host operation, both the packaged field's entity (PLG004) and the gate's
        // target (PLG002) are unknown — the first one hit fails the build.
        var builder = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string> { ["plugins.pkg.title"] = "Pkg", ["ext.pkg.priority"] = "Priority" })
            .AddPlugin<PackagingPlugin>();
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("PLG00", error.Message);
    }

    [Fact]
    public void Manifest_omits_inactive_plugins_and_lists_active_ones()
    {
        var model = new TamModelBuilder()
            .AddPlugin<WellBehavedPlugin>()
            .Build();
        var overlay = new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>();

        var inactive = ManifestBuilder.Build(model, overlay, revision: 0, new HashSet<string>());
        Assert.DoesNotContain("demo.things.create", inactive.Operations.Keys);
        Assert.Empty(inactive.Plugins);

        var active = ManifestBuilder.Build(model, overlay, revision: 0, new HashSet<string> { "demo" });
        Assert.Contains("demo.things.create", active.Operations.Keys);
        Assert.Equal(["demo"], active.Plugins);
        Assert.Equal("demo", active.Operations["demo.things.create"].Plugin);

        // null = export mode: everything included (the D4 baseline covers all plugins).
        var export = ManifestBuilder.Build(model, overlay, revision: 0);
        Assert.Contains("demo.things.create", export.Operations.Keys);
    }

    [TamPlugin("dup")]
    private sealed class DuplicateFieldPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.dup.title"] = "Dup",
                ["ext.dup.x"] = "X",
            });
            plugin.ExtensionField("thing", "x", "integer");
            plugin.ExtensionField("thing", "x", "integer");   // same key twice → PLG004
        }
    }

    [Fact]
    public void PLG004_rejects_duplicate_packaged_field_keys_in_one_plugin()
    {
        var builder = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["operations.host.things.close.title"] = "Close",
                ["labels.name"] = "Name",
            })
            .AddOperationType(typeof(CloseThing))
            .AddPlugin<DuplicateFieldPlugin>();
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("PLG004", error.Message);
    }

    [Fact]
    public void Plugin_id_must_match_the_wire_prefix_grammar()
    {
        // A bad id would produce nonsense namespace prefixes — rejected at AddPlugin.
        var error = Assert.Throws<InvalidOperationException>(
            () => new TamModelBuilder().AddPlugin<BadIdPlugin>());
        Assert.Contains("PLG000", error.Message);
    }

    [TamPlugin("Bad.Id")]
    private sealed class BadIdPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) { }
    }
}
