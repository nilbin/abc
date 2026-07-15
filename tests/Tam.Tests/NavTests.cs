using Tam;

namespace Tam.Tests;

/// <summary>
/// The docs/30 nav merge: host layout + package/plugin contributions + mechanical fallback,
/// NAV diagnostics, and the manifest's activation filtering.
/// </summary>
public class NavTests
{
    // ---- shared host surface: one view + grid the host tree can bind ----------------------

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

    private static TamModelBuilder HostWithGrid(IReadOnlyDictionary<string, string>? extra = null)
    {
        var keys = new Dictionary<string, string>
        {
            ["labels.id"] = "Id",
            ["labels.name"] = "Name",
            ["nav.work"] = "Work",
            ["nav.things"] = "Things",
            ["nav.more"] = "More",
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string>()) keys[k] = v;
        return new TamModelBuilder()
            .LocaleDefaults("en", keys)
            .AddViewType(typeof(ThingsList))
            .Grid<ThingsList.Result>("web.things", "things.list", grid =>
                grid.Column(x => x.Name));
    }

    // ---- a contributing plugin (namespaced ids, its own grid) -----------------------------

    [TamPlugin("demo")]
    private sealed class ContributingPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.demo.title"] = "Demo",
                ["demo.labels.name"] = "Name",
                ["nav.demo.things"] = "Demo things",
            });
            plugin.Nav(nav => nav.Page("demo.things", grid: "demo.things.grid",
                suggest: "administration", order: 20));
            plugin.Model
                .AddViewType(typeof(DemoList))
                .Grid<DemoList.Result>("demo.things.grid", "demo.things.list", grid =>
                    grid.Column(x => x.Name));
        }
    }

    [View("demo.things.list")]
    [Authorize("demo.things.read")]
    private static class DemoList
    {
        public sealed record Query;

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("demo.labels.name")]
            public string Name { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();
    }

    // ---- merge behavior --------------------------------------------------------------------

    [Fact]
    public void Sections_collect_suggestions_after_explicit_children_in_order()
    {
        var model = HostWithGrid(new Dictionary<string, string>
            {
                ["nav.admin"] = "Admin",
                ["nav.administration"] = "General",
                ["nav.things2"] = "Things 2",
            })
            .AddPlugin<ContributingPlugin>()
            .Nav("web", nav => nav
                .Mode("work", m => m.Page("things", grid: "web.things", order: 10))
                .Mode("admin", m => m.Section("administration", s =>
                    s.Page("things2", grid: "web.things", order: 90))))
            .Build();

        var admin = model.Nav["web"][1];
        var section = Assert.Single(admin.Children);
        Assert.Equal("administration", section.Id);
        // order 20 (contribution) sorts before order 90 (explicit child)
        Assert.Equal(["demo.things", "things2"], section.Children.Select(c => c.Id));
        Assert.Equal("demo", section.Children[0].Plugin);
        // collected — NOT duplicated into "more"
        Assert.DoesNotContain(model.Nav["web"].SelectMany(Flatten), n => n.Id == NavNode.More);
    }

    [Fact]
    public void Place_overrides_a_suggestion_and_NAV003_rejects_unknown_ids()
    {
        var model = HostWithGrid()
            .AddPlugin<ContributingPlugin>()
            .Nav("web", nav => nav.Mode("work", m => m
                .Page("things", grid: "web.things")
                .Place("demo.things")))
            .Build();

        var work = model.Nav["web"][0];
        Assert.Equal(["things", "demo.things"], work.Children.Select(c => c.Id));
        Assert.Equal("demo.things.grid", work.Children[1].Target!.Grid);
        Assert.DoesNotContain(model.Nav["web"].SelectMany(Flatten), n => n.Id == NavNode.More);

        var bad = HostWithGrid()
            .Nav("web", nav => nav.Mode("work", m => m.Place("nobody.declared.this")));
        var error = Assert.Throws<InvalidOperationException>(() => bad.Build());
        Assert.StartsWith("NAV003", error.Message);
    }

    [Fact]
    public void Uncollected_content_falls_back_to_the_more_section_in_the_last_mode()
    {
        // No section matches the suggestion and the plugin's grid is never referenced:
        // the CONTRIBUTED page (not the generic plugin page) lands under "more".
        var model = HostWithGrid()
            .AddPlugin<ContributingPlugin>()
            .Nav("web", nav => nav.Mode("work", m => m.Page("things", grid: "web.things")))
            .Build();

        var work = model.Nav["web"][0];
        var more = work.Children.Single(c => c.Id == NavNode.More);
        var page = Assert.Single(more.Children);
        Assert.Equal("demo.things", page.Id);
        Assert.Equal("demo.things.grid", page.Target!.Grid);
    }

    [TamPlugin("silent")]
    private sealed class NavlessPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.silent.title"] = "Silent",
                ["silent.labels.name"] = "Name",
            });
            plugin.Model
                .AddViewType(typeof(SilentList))
                .Grid<SilentList.Result>("silent.grid", "silent.list", grid =>
                    grid.Column(x => x.Name));
        }
    }

    [View("silent.list")]
    [Authorize("silent.read")]
    private static class SilentList
    {
        public sealed record Query;

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("silent.labels.name")]
            public string Name { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();
    }

    [Fact]
    public void A_plugin_that_declares_no_nav_still_appears_as_a_generic_plugin_page()
    {
        var model = HostWithGrid()
            .AddPlugin<NavlessPlugin>()
            .Nav("web", nav => nav.Mode("work", m => m.Page("things", grid: "web.things")))
            .Build();

        var more = model.Nav["web"][0].Children.Single(c => c.Id == NavNode.More);
        var page = Assert.Single(more.Children);
        Assert.Equal("silent", page.Id);
        Assert.Equal("silent", page.Target!.Plugin);
        Assert.Equal("plugins.silent.title", page.LabelKey);
    }

    // ---- diagnostics -----------------------------------------------------------------------

    [Fact]
    public void NAV000_layout_is_the_hosts_alone()
    {
        var builder = new TamModelBuilder();
        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPlugin<LayoutGrabbingPlugin>().Build());
        Assert.StartsWith("NAV000", error.Message);
    }

    [TamPlugin("grabby")]
    private sealed class LayoutGrabbingPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.Model.Nav("web", nav => nav.Mode("mine"));
    }

    [Fact]
    public void NAV001_rejects_duplicate_node_ids_per_surface()
    {
        var builder = HostWithGrid()
            .Nav("web", nav => nav.Mode("work", m => m
                .Page("things", grid: "web.things")
                .Page("things", grid: "web.things")));
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.StartsWith("NAV001", error.Message);
    }

    [Fact]
    public void NAV002_enforces_the_depth_cap()
    {
        var builder = HostWithGrid(new Dictionary<string, string>
            {
                ["nav.a"] = "a", ["nav.b"] = "b", ["nav.c"] = "c", ["nav.d"] = "d",
            })
            .Nav("web", nav => nav.Mode("work", m => m
                .Section("a", a => a.Page("b", grid: "web.things", children: b =>
                    b.Page("c", grid: "web.things", children: c =>
                        c.Page("d", grid: "web.things"))))));
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.StartsWith("NAV002", error.Message);
    }

    [Fact]
    public void NAV004_rejects_unknown_grid_targets()
    {
        var builder = HostWithGrid()
            .Nav("web", nav => nav.Mode("work", m => m.Page("things", grid: "web.nope")));
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.StartsWith("NAV004", error.Message);
    }

    [Fact]
    public void NAV005_page_targets_require_an_existing_catalogue_atom()
    {
        var missing = HostWithGrid()
            .Nav("web", nav => nav.Mode("work", m => m.Page("things", page: "custom")));
        Assert.StartsWith("NAV005",
            Assert.Throws<InvalidOperationException>(() => missing.Build()).Message);

        var unknown = HostWithGrid()
            .Nav("web", nav => nav.Mode("work", m => m
                .Page("things", page: "custom", permission: "not.an.atom")));
        Assert.StartsWith("NAV005",
            Assert.Throws<InvalidOperationException>(() => unknown.Build()).Message);

        var ok = HostWithGrid()
            .Nav("web", nav => nav.Mode("work", m => m
                .Page("things", page: "custom", permission: "things.read")))
            .Build();
        Assert.Equal("things.read", ok.Nav["web"][0].Children[0].Permission);
    }

    [Fact]
    public void L10N001_requires_every_nav_label_key()
    {
        var builder = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["labels.name"] = "Name",
                // "nav.work" deliberately missing
            })
            .AddViewType(typeof(ThingsList))
            .Grid<ThingsList.Result>("web.things", "things.list", grid => grid.Column(x => x.Name))
            .Nav("web", nav => nav.Mode("work"));
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.StartsWith("L10N001", error.Message);
        Assert.Contains("nav.work", error.Message);
    }

    // ---- manifest --------------------------------------------------------------------------

    [Fact]
    public void Manifest_prunes_an_inactive_plugins_nav_nodes()
    {
        var model = HostWithGrid(new Dictionary<string, string>
            {
                ["nav.admin"] = "Admin",
                ["nav.administration"] = "General",
            })
            .AddPlugin<ContributingPlugin>()
            .Nav("web", nav => nav
                .Mode("work", m => m.Page("things", grid: "web.things"))
                .Mode("admin", m => m.Section("administration")))
            .Build();

        var extensions = new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>();

        var active = ManifestBuilder.Build(model, extensions, 0, new HashSet<string> { "demo" });
        var activeAdmin = active.Nav!["web"].Single(n => n.Id == "admin");
        Assert.Contains(activeAdmin.Children.Single(c => c.Id == "administration").Children,
            c => c.Id == "demo.things");

        var inactive = ManifestBuilder.Build(model, extensions, 0, new HashSet<string>());
        var inactiveAdmin = inactive.Nav!["web"].Single(n => n.Id == "admin");
        Assert.Empty(inactiveAdmin.Children.Single(c => c.Id == "administration").Children);
        // host nodes survive untouched
        Assert.Contains(inactive.Nav["web"].Single(n => n.Id == "work").Children,
            c => c.Id == "things");
    }

    private static IEnumerable<NavNode> Flatten(NavNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Flatten));
}
