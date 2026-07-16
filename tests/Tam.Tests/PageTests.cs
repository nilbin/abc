using Tam;

namespace Tam.Tests;

/// <summary>Framework-composed pages (docs/32): PAGE001 build gates, the manifest shape, the
/// NAV005 relaxation for declared pages, and readOnly packaged-field manifest mapping.</summary>
public class PageTests
{
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

    [View("things.detail")]
    [Authorize("things.read")]
    private static class ThingsDetail
    {
        public sealed record Query(Guid ThingId);

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
            ["labels.id"] = "Id", ["labels.name"] = "Name", ["nav.work"] = "Work",
            ["nav.things"] = "Things", ["nav.more"] = "More",
        })
        .AddViewType(typeof(ThingsList))
        .AddViewType(typeof(ThingsDetail))
        .Grid<ThingsList.Result>("web.things", "things.list", g => g.Column(x => x.Name))
        .Slot("web.things.detail", slot => slot.Key("thingId"));

    [Fact]
    public void A_declared_page_lands_in_manifest_and_relaxes_NAV005()
    {
        var model = Host()
            .Page("things", page => page
                .Grid("web.things")
                .Record(record => record
                    .Detail("things.detail", key: "thingId")
                    .Title("name")
                    .Slot("web.things.detail")))
            // page target WITHOUT explicit permission: legal because the page is declared
            .Nav("web", nav => nav.Mode("work", m => m.Page("things", page: "things")))
            .Build();

        var manifest = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0, null);
        var page = manifest.Pages["things"];
        Assert.Equal("web.things", page.Grid);
        Assert.Equal(("things.detail", "thingId", "name"),
            (page.Record!.DetailView, page.Record.Key, page.Record.TitleField));
        Assert.Equal(["web.things.detail"], page.Record.Slots);
        Assert.Null(model.Nav["web"][0].Children[0].Permission);
    }

    [Fact]
    public void NAV005_still_fires_for_undeclared_page_targets_without_permission()
    {
        var builder = Host()
            .Nav("web", nav => nav.Mode("work", m => m.Page("things", page: "custom-react")));
        Assert.StartsWith("NAV005",
            Assert.Throws<InvalidOperationException>(() => builder.Build()).Message);
    }

    [Fact]
    public void PAGE001_rejects_broken_compositions()
    {
        Assert.StartsWith("PAGE001", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.nope")).Build()).Message);

        Assert.StartsWith("PAGE001", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "wrongKey"))).Build()).Message);

        Assert.StartsWith("PAGE001", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId").Title("ghost"))).Build()).Message);

        Assert.StartsWith("PAGE001", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId")
                    .Slot("web.nowhere"))).Build()).Message);
    }

    [TamPlugin("meter")]
    private sealed class ReadOnlyFieldPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.meter.title"] = "Meter",
                ["ext.meter.reading"] = "Reading",
            });
            plugin.ExtensionField("thing", "reading", "number", readOnly: true);
        }
    }

    private sealed class Thing : IExtensible
    {
        public ExtensionData Extensions { get; set; } = new();
    }

    [Operation("things.touch")]
    [Authorize("things.manage")]
    [AcceptsExtensions(typeof(Thing))]
    private static class TouchThing
    {
        public sealed record Input([property: LabelKey("labels.name")] string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public void ReadOnly_packaged_fields_carry_the_flag_into_specs()
    {
        var model = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["labels.name"] = "Name", ["operations.things.touch.title"] = "Touch",
            })
            .AddOperationType(typeof(TouchThing))
            .AddPlugin<ReadOnlyFieldPlugin>()
            .Build();

        var packaged = Assert.Single(model.PackagedFields);
        Assert.True(packaged.Spec.ReadOnly);
        Assert.True(ManifestBuilder.ToField(packaged.Spec).ReadOnly);
    }
}
