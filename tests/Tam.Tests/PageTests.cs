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
        public sealed record Query(string? Search = null);

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

    [Operation("things.rename")]
    [Authorize("things.manage")]
    private static class RenameThing
    {
        public sealed record Input(
            [property: LabelKey("labels.id")] Guid ThingId,
            [property: LabelKey("labels.name")] string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    [Operation("things.keyless")]
    [Authorize("things.manage")]
    private static class KeylessRename
    {
        public sealed record Input([property: LabelKey("labels.name")] string Name);

        public static Task<Result> Execute(Input input, OperationContext context) =>
            Task.FromResult(Result.Success());
    }

    private static TamModelBuilder Host() => new TamModelBuilder()
        .LocaleDefaults("en", new Dictionary<string, string>
        {
            ["labels.id"] = "Id", ["labels.name"] = "Name", ["nav.work"] = "Work",
            ["nav.things"] = "Things", ["nav.more"] = "More",
            ["operations.things.rename.title"] = "Rename",
            ["operations.things.keyless.title"] = "Rename",
        })
        .AddViewType(typeof(ThingsList))
        .AddViewType(typeof(ThingsDetail))
        .Grid<ThingsList.Result>("web.things", "things.list", g => g.Column(x => x.Name))
        .AddOperationType(typeof(RenameThing))
        .Form<RenameThing.Input>("web.things.edit", "things.rename", form => form.Field(x => x.Name))
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
                    .Slot("web.things.detail")     // before the form: renders above it
                    .Form("web.things.edit")))
            // page target WITHOUT explicit permission: legal because the page is declared
            .Nav("web", nav => nav.Mode("work", m => m.Page("things", page: "things")))
            .Build();

        var manifest = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0, null);
        var page = manifest.Pages["things"];
        Assert.Equal([("grid", "web.things")],
            page.Sections.Select(sec => (sec.Kind, sec.Id)));
        Assert.Equal(("things.detail", "thingId", "name"),
            (page.Record!.DetailView, page.Record.Key, page.Record.TitleField));
        // Flat authoring normalizes to ONE implicit heading-less tab; declaration order IS
        // layout order: slot declared BEFORE the form renders above it. A plain form/slot
        // record derives the MODAL display semantic (docs/32).
        var implicit_ = Assert.Single(page.Record.Tabs);
        Assert.Null(implicit_.HeadingKey);
        Assert.Equal("modal", page.Record.Display);
        Assert.Equal([("slot", "web.things.detail"), ("form", "web.things.edit")],
            implicit_.Sections.Select(sec => (sec.Kind, sec.Id)));
        Assert.Null(model.Nav["web"][0].Children[0].Permission);
    }

    [Fact]
    public void Record_display_derives_from_structure_and_the_override_wins()
    {
        // Several tabs → a workspace: page.
        ManifestRecord Record(Action<RecordBuilder> configure)
        {
            var model = Host()
                .LocaleDefaults("en", new Dictionary<string, string>
                {
                    ["tabs.a"] = "A", ["tabs.b"] = "B",
                })
                .Page("things", page =>
                {
                    page.Grid("web.things");
                    page.Record(configure);
                })
                .Build();
            return ManifestBuilder.Build(
                model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0, null)
                .Pages["things"].Record!;
        }

        Assert.Equal("page", Record(r => r.Detail("things.detail", key: "thingId")
            .Tab("a", "tabs.a", s => s.Form("web.things.edit"))
            .Tab("b", "tabs.b", s => s.Slot("web.things.detail"))).Display);

        // A single flat child GRID also makes a workspace.
        Assert.Equal("page", Record(r => r.Detail("things.detail", key: "thingId")
            .Slot("web.things.detail")
            .Grid("web.things", b => b.Query("search", fromRecord: "name"))).Display);

        // The explicit override beats the derivation in both directions.
        Assert.Equal("modal", Record(r => r.Detail("things.detail", key: "thingId")
            .Display(RecordDisplay.Modal)
            .Tab("a", "tabs.a", s => s.Form("web.things.edit"))
            .Tab("b", "tabs.b", s => s.Slot("web.things.detail"))).Display);
        Assert.Equal("page", Record(r => r.Detail("things.detail", key: "thingId")
            .Display(RecordDisplay.Page)
            .Form("web.things.edit").Slot("web.things.detail")).Display);
    }

    [Fact]
    public void Record_tabs_export_with_binds_and_panel_tab_markers()
    {
        var model = Host()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["tabs.details"] = "Details", ["tabs.related"] = "Related",
            })
            .Page("things", page => page
                .Grid("web.things")
                .Record(record => record
                    .Detail("things.detail", key: "thingId")
                    .Tab("details", "tabs.details", s => s.Form("web.things.edit"))
                    .Tab("related", "tabs.related", s => s
                        .Grid("web.things", bind => bind.Query("search", fromRecord: "name")))
                    .PanelTabs("web.things.detail")))
            .Build();

        var manifest = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0, null);
        var tabs = manifest.Pages["things"].Record!.Tabs;
        Assert.Equal(3, tabs.Count);
        Assert.Equal(("details", "tabs.details"), (tabs[0].Id, tabs[0].HeadingKey));
        var bound = Assert.Single(tabs[1].Sections);
        Assert.Equal(new Dictionary<string, string> { ["search"] = "name" }, bound.Bind);
        // The panel-tabs marker: no sections of its own, the slot to expand client-side.
        Assert.Equal("web.things.detail", tabs[2].Slot);
        Assert.Empty(tabs[2].Sections);
        // The marker references the slot like a slot section: auto-declared with the key.
        Assert.Equal(["thingId"], model.Slots["web.things.detail"].ContextKeys);
    }

    [Fact]
    public void PAGE001_rejects_broken_record_tabs()
    {
        // Flat sections AND tabs on one record.
        Assert.Contains("flat sections OR tabs", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId")
                    .Form("web.things.edit")
                    .Tab("t", "tabs.t", s => s.Slot("web.things.detail")))).Build()).Message);

        // Duplicate tab ids (camelization collides "Details" and "details").
        Assert.Contains("duplicate record tab id", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId")
                    .Tab("Details", "tabs.a", s => s.Form("web.things.edit"))
                    .Tab("details", "tabs.b", s => s.Slot("web.things.detail")))).Build()).Message);

        // A record grid section that is not a declared grid.
        Assert.Contains("not a declared grid", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId")
                    .Grid("web.ghost"))).Build()).Message);

        // A bind FIELD that is not on the detail view.
        Assert.Contains("not a result field", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId")
                    .Grid("web.things", b => b.Query("search", fromRecord: "ghost")))).Build()).Message);

        // A bind PARAM the target view neither takes nor filters on: the server ignores
        // unknown params, so this would silently show EVERY row — a build error instead.
        Assert.Contains("unfiltered", Assert.Throws<InvalidOperationException>(() =>
            Host().Page("p", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId")
                    .Grid("web.things", b => b.Query("ghostParam", fromRecord: "name")))).Build()).Message);
    }

    [Fact]
    public void L10N001_gates_record_tab_headings()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Host()
            .Page("things", page => page.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId")
                    .Tab("details", "tabs.missing-heading", s => s
                        .Form("web.things.edit").Slot("web.things.detail"))))
            .Build());
        Assert.StartsWith("L10N001", error.Message);
        Assert.Contains("tabs.missing-heading", error.Message);
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

        // review-round-4 F6: a record form whose operation lacks the KEY input builds a form
        // that can never carry the record identity — PAGE001, not a silent broken modal.
        var keyless = Assert.Throws<InvalidOperationException>(() => Host()
            .AddOperationType(typeof(KeylessRename))
            .Form<KeylessRename.Input>("web.things.keyless", "things.keyless", f => f.Field(x => x.Name))
            .Page("things", p => p.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId").Form("web.things.keyless"))).Build());
        Assert.StartsWith("PAGE001", keyless.Message);
        Assert.Contains("could never prefill", keyless.Message);

        // docs/34 M5 fix 9: an undeclared record slot no longer PAGE001s — placement IS
        // declaration, and the auto-declared slot carries the record's context key.
        var auto = Host().Page("p", page => page.Grid("web.things")
            .Record(r => r.Detail("things.detail", key: "thingId")
                .Slot("web.things.detail")     // the fixture's standalone declaration, placed
                .Slot("web.auto"))).Build();   // never declared — placement declares it
        Assert.Equal(["thingId"], auto.Slots["web.auto"].ContextKeys);
        Assert.False(auto.Slots["web.auto"].External);
    }

    [Fact]
    public void SLOT001_catches_orphaned_slots_and_external_exempts_them()
    {
        // declared, referenced by no page, not external → authored into invisibility
        var orphan = Host();   // Host declares web.things.detail and no page references it
        Assert.StartsWith("SLOT001",
            Assert.Throws<InvalidOperationException>(() => orphan.Build()).Message);

        // external: the app places it in React — the model stops policing
        var external = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string> { ["labels.id"] = "Id", ["labels.name"] = "Name" })
            .AddViewType(typeof(ThingsList))
            .Grid<ThingsList.Result>("web.things", "things.list", g => g.Column(x => x.Name))
            .Slot("web.custom.surface", slot => slot.Key("thingId"), external: true)
            .Build();
        Assert.True(external.Slots["web.custom.surface"].External);
    }

    [Fact]
    public void Pages_compose_multiple_grids_and_page_level_slots_in_order()
    {
        var model = Host()
            .Slot("web.things.banner")   // page-level, no context keys
            .Page("things", page => page
                .Slot("web.things.banner")
                .Grid("web.things")
                .Grid("web.things2")
                .Record(r => r.Detail("things.detail", key: "thingId").Slot("web.things.detail")))
            .Grid<ThingsList.Result>("web.things2", "things.list", g => g.Column(x => x.Name))
            .Build();

        var page = model.Pages["things"];
        Assert.Equal([("slot", "web.things.banner"), ("grid", "web.things"), ("grid", "web.things2")],
            page.Sections.Select(sec => (sec.Kind, sec.Id)));
        Assert.Equal("web.things", page.PrimaryGridId);   // FIRST grid opens the record
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

    [TamPlugin("pgdemo")]
    private sealed class PagePlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.pgdemo.title"] = "Demo",
                ["pgdemo.labels.name"] = "Name",
                ["nav.pgdemo.things"] = "Demo things",
            });
            plugin.Model.AddViewType(typeof(PluginThingsList));
            plugin.Grid<PluginThingsList.Result>("pgdemo.web.things", "pgdemo.things.list");
            plugin.Page("pgdemo.things", page => page.Grid("pgdemo.web.things"));
        }
    }

    [View("pgdemo.things.list")]
    [Authorize("pgdemo.read")]
    private static class PluginThingsList
    {
        public sealed record Query;

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("pgdemo.labels.name")]
            public string Name { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();
    }

    [TamPlugin("squat")]
    private sealed class SquattingPagePlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.squat.title"] = "Squat",
            });
            // A page OUTSIDE the plugin namespace — PLG001, like any other contribution.
            plugin.Page("orders", page => page.Grid("web.things"));
        }
    }

    [Fact]
    public void A_plugin_page_is_tagged_filtered_and_namespaced()
    {
        var model = Host()
            .Page("things", p => p.Grid("web.things")
                .Record(r => r.Detail("things.detail", key: "thingId")
                    .Form("web.things.edit").Slot("web.things.detail")))
            .AddPlugin<PagePlugin>().Build();
        Assert.Equal("pgdemo", model.Pages["pgdemo.things"].Plugin);

        // Activation filtering: inactive plugin → the page does not exist for the tenant.
        var inactive = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0,
            new HashSet<string>());
        Assert.False(inactive.Pages.ContainsKey("pgdemo.things"));
        var active = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0,
            new HashSet<string> { "pgdemo" });
        Assert.True(active.Pages.ContainsKey("pgdemo.things"));

        // Outside the namespace → PLG001.
        Assert.StartsWith("PLG001", Assert.Throws<InvalidOperationException>(
            () => Host()
                .Page("things", p => p.Grid("web.things")
                    .Record(r => r.Detail("things.detail", key: "thingId")
                        .Form("web.things.edit").Slot("web.things.detail")))
                .AddPlugin<SquattingPagePlugin>().Build()).Message);
    }
}
