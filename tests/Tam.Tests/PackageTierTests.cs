using Tam;
using Tam.AspNetCore.SystemOps;

namespace Tam.Tests;

/// <summary>
/// The framework-package tier (docs/22): same authoring surface as plugins, always active,
/// validated against CLAIMED wire prefixes instead of an id namespace.
/// </summary>
public class PackageTierTests
{
    [TamPackage("tam.things", "things", "web.things")]
    private sealed class ThingsPackage : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["operations.things.create.title"] = "Create thing",
                ["labels.name"] = "Name",
            });
            plugin.Model.AddOperationType(typeof(CreateThing));
        }
    }

    [Operation("things.create")]
    [Authorize("things.manage")]
    private static class CreateThing
    {
        public sealed record Input([property: LabelKey("labels.name")] string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public void Package_contributions_are_tagged_and_listed()
    {
        var model = new TamModelBuilder().AddPackage<ThingsPackage>().Build();

        Assert.Equal("tam.things", model.Operations["things.create"].Plugin);
        Assert.True(model.Packages.ContainsKey("tam.things"));
        Assert.False(model.Plugins.ContainsKey("tam.things"));   // not activatable
    }

    [Fact]
    public void Package_entries_survive_an_empty_activation_set()
    {
        // The always-active property at the manifest: a tenant with NO activation rows still
        // sees every package contribution (a plugin's would be omitted).
        var model = new TamModelBuilder().AddPackage<ThingsPackage>().Build();
        var manifest = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0,
            new HashSet<string>());
        Assert.Contains("things.create", manifest.Operations.Keys);
        Assert.Empty(manifest.Plugins);   // and it is NOT listed as an activatable plugin
    }

    [TamPackage("tam.escapee", "things")]
    private sealed class EscapingPackage : ITamPlugin
    {
        // Registers an operation outside its claimed prefixes — must fail PLG001.
        public void Configure(PluginBuilder plugin) =>
            plugin.Model.AddOperationType(typeof(EscapedOp));
    }

    [Operation("orders.hijack")]
    [Authorize("orders.hijack")]
    private static class EscapedOp
    {
        public sealed record Input(string Name);
        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public void PLG001_rejects_contributions_outside_claimed_prefixes()
    {
        var builder = new TamModelBuilder().AddPackage<EscapingPackage>();
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.StartsWith("PLG001", error.Message);
        Assert.Contains("claimed prefixes", error.Message);
    }

    [TamPackage("tam.empty")]
    private sealed class NoPrefixPackage : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) { }
    }

    [Fact]
    public void A_package_must_claim_at_least_one_prefix()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new TamModelBuilder().AddPackage<NoPrefixPackage>());
        Assert.StartsWith("PKG000", error.Message);
    }

    [Fact]
    public void The_system_module_is_eleven_packages()
    {
        // The aggregate registers the framework's admin surface as packages — every framework
        // capability exercises the plugin seams. Forms/grids ship WITH the packages now.
        var model = new TamModelBuilder()
            .DefaultCulture("sv")
            .AddTamSystem()
            .Build();

        Assert.Equal(11, model.Packages.Count);
        Assert.Equal("tam.users", model.Operations["users.invite"].Plugin);
        Assert.Equal("tam.audit", model.Views["audit.entries"].Plugin);
        Assert.Equal("tam.users", model.Grids["web.users"].Plugin);
        Assert.Equal("tam.extensions", model.Forms["web.extensions.define"].Plugin);

        // Always-active at the manifest, even for a tenant with zero activation rows.
        var manifest = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0,
            new HashSet<string>());
        Assert.Contains("users.invite", manifest.Operations.Keys);
        Assert.Contains("web.users", manifest.Grids.Keys);
    }
}
