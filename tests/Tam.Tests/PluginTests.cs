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
}
