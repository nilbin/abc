using Tam;

namespace Tam.Tests;

/// <summary>
/// Cross-domain plugin seams (docs/31): grid action contributions (D-X1, PLG006) and declared
/// view requirements (D-X3, PLG008) — the build gates and the manifest's activation filtering.
/// The runtime writer/reader are wire-verified (STATUS records the invoicing suite).
/// </summary>
public class CrossDomainTests
{
    // ---- host surface: one view + grid to contribute onto -------------------------------

    [View("things.list")]
    [Authorize("things.read")]
    private static class ThingsList
    {
        public sealed record Query;

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("labels.name")]
            public string Name { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();
    }

    private static TamModelBuilder Host() => new TamModelBuilder()
        .LocaleDefaults("en", new Dictionary<string, string>
        {
            ["labels.id"] = "Id",
            ["labels.name"] = "Name",
            ["plugins.billing.title"] = "Billing",
            ["operations.billing.charge.title"] = "Charge",
            ["billing.labels.thing"] = "Thing",
        })
        .AddViewType(typeof(ThingsList))
        .Grid<ThingsList.Result>("web.things", "things.list", grid => grid.Column(x => x.Name));

    [TamPlugin("billing")]
    private sealed class BillingPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.Model.AddOperationType(typeof(Charge));
            plugin.RequiresView("things.list", "id", "name");
            plugin.GridAction("web.things", "billing.charge",
                bind => bind.Field("thingId", fromColumn: "id"));
        }
    }

    [Operation("billing.charge")]
    [Authorize("billing.manage")]
    private static class Charge
    {
        public sealed record Input(
            [property: LabelKey("billing.labels.thing")] Guid ThingId);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public void Contribution_and_requirement_land_in_the_model_and_manifest()
    {
        var model = Host().AddPlugin<BillingPlugin>().Build();

        var action = Assert.Single(model.GridActions["web.things"]);
        Assert.Equal(("billing.charge", "billing"), (action.OperationId, action.PluginId));
        Assert.Equal([("thingId", "id")], action.Bind);
        var requirement = Assert.Single(model.ViewRequirements);
        Assert.Equal("things.list", requirement.ViewId);

        // ACTIVE tenant: the contributed action is on the grid; host actions untouched.
        var extensions = new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>();
        var active = ManifestBuilder.Build(model, extensions, 0, new HashSet<string> { "billing" });
        var contributed = Assert.Single(active.Grids["web.things"].ContributedActions);
        Assert.Equal("billing.charge", contributed.Operation);
        Assert.Equal("id", contributed.Bind["thingId"]);
        Assert.Empty(active.Grids["web.things"].RowActions);

        // INACTIVE tenant: the action does not exist, like every plugin contribution.
        var inactive = ManifestBuilder.Build(model, extensions, 0, new HashSet<string>());
        Assert.Empty(inactive.Grids["web.things"].ContributedActions);
        Assert.DoesNotContain("billing.charge", inactive.Operations.Keys);
    }

    [TamPlugin("rogue")]
    private sealed class ForeignOperationPlugin : ITamPlugin
    {
        // Tries to attach the HOST's operation as its own grid action — PLG006.
        public void Configure(PluginBuilder plugin) =>
            plugin.GridAction("web.things", "things.create", b => b.Field("id", "id"));
    }

    [TamPlugin("typo")]
    private sealed class BadBindPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.Model.AddOperationType(typeof(TypoCharge));
            plugin.GridAction("web.things", "typo.charge",
                bind => bind.Field("thingId", fromColumn: "nope"));
        }
    }

    [Operation("typo.charge")]
    [Authorize("typo.manage")]
    private static class TypoCharge
    {
        public sealed record Input(
            [property: LabelKey("labels.name")] Guid ThingId);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public void PLG006_rejects_foreign_operations_and_unknown_bind_columns()
    {
        var foreign = Host().AddPlugin<ForeignOperationPlugin>();
        Assert.StartsWith("PLG006",
            Assert.Throws<InvalidOperationException>(() => foreign.Build()).Message);

        var typo = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["labels.id"] = "Id", ["labels.name"] = "Name",
                ["plugins.typo.title"] = "T", ["operations.typo.charge.title"] = "C",
            })
            .AddViewType(typeof(ThingsList))
            .Grid<ThingsList.Result>("web.things", "things.list", g => g.Column(x => x.Name))
            .AddPlugin<BadBindPlugin>();
        Assert.StartsWith("PLG006",
            Assert.Throws<InvalidOperationException>(() => typo.Build()).Message);
    }

    [TamPlugin("greedy")]
    private sealed class UndeclaredFieldPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.RequiresView("things.list", "id", "secretMargin");
    }

    [Fact]
    public void PLG008_rejects_requirements_the_host_does_not_expose()
    {
        var missing = Host().AddPlugin<UndeclaredFieldPlugin>();
        Assert.StartsWith("PLG008",
            Assert.Throws<InvalidOperationException>(() => missing.Build()).Message);
    }
}
