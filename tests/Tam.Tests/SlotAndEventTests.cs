using Tam;

namespace Tam.Tests;

/// <summary>docs/31 phase 2: host-declared slots + plugin panels (D-X4, PLG007) and event
/// contracts (D-X5, PLG009) — build gates and manifest filtering.</summary>
public class SlotAndEventTests
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

    [View("notes.for-thing")]
    [Authorize("notes.read")]
    private static class NoteList
    {
        public sealed record Query(Guid? ThingId = null);

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("notes.labels.text")]
            public string Text { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();
    }

    private static TamModelBuilder Host() => new TamModelBuilder()
        .LocaleDefaults("en", new Dictionary<string, string>
        {
            ["labels.id"] = "Id",
            ["labels.name"] = "Name",
            ["plugins.notes.title"] = "Notes",
            ["plugins.dependent.title"] = "Dependent",
            ["notes.labels.text"] = "Text",
        })
        .AddViewType(typeof(ThingsList))
        .Grid<ThingsList.Result>("web.things", "things.list", g => g.Column(x => x.Name))
        .Slot("web.things.detail", slot => slot.Key("thingId"), external: true)
        .PublishesEvent("thing-archived", "thingId:guid", "name");

    [TamPlugin("notes")]
    private sealed class NotesPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.Model.AddViewType(typeof(NoteList));
            plugin.Model.Grid<NoteList.Result>("notes.web.list", "notes.for-thing",
                g => g.Column(x => x.Text));
            plugin.Panel("web.things.detail", grid: "notes.web.list",
                bind => bind.Query("thingId", fromContext: "thingId"));
            plugin.RequiresEvent("thing-archived", "thingId");
            plugin.OnEffect<Handler>("thing-archived");
            plugin.PublishesEvent("notes.note-added", "noteId:guid");
        }
    }

    private sealed class Handler : IEffectHandler
    {
        public Task HandleAsync(EffectEvent effect, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void Panels_and_events_land_in_the_manifest_activation_filtered()
    {
        var model = Host().AddPlugin<NotesPlugin>().Build();
        var extensions = new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>();

        var active = ManifestBuilder.Build(model, extensions, 0, new HashSet<string> { "notes" });
        var panel = Assert.Single(active.Slots["web.things.detail"]);
        Assert.Equal(("notes.web.list", "notes"), (panel.Grid, panel.Plugin));
        Assert.Equal("thingId", panel.Bind["thingId"]);
        Assert.Equal(["notes"], active.Events["thing-archived"].SubscribedBy);
        Assert.Equal(["thingId", "name"], active.Events["thing-archived"].Fields);
        // The manifest carries the publisher's declared kinds — the contract artifact
        // downstream generators read (docs/31).
        Assert.Equal("guid", active.Events["thing-archived"].Kinds["thingId"]);
        Assert.False(active.Events["thing-archived"].Kinds.ContainsKey("name"));

        var inactive = ManifestBuilder.Build(model, extensions, 0, new HashSet<string>());
        Assert.Empty(inactive.Slots["web.things.detail"]);   // slot exists, panel does not
        Assert.Empty(inactive.Events["thing-archived"].SubscribedBy);
    }

    [View("squatter.list")]
    [Authorize("squatter.read")]
    private static class SquatterList
    {
        public sealed record Query(Guid? ThingId = null);

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("notes.labels.text")]
            public string Text { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();
    }

    [TamPlugin("squatter")]
    private sealed class UnknownSlotPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.LocaleDefaults("en", new Dictionary<string, string>
            {
                ["plugins.squatter.title"] = "S",
            });
            plugin.Model.AddViewType(typeof(SquatterList));
            plugin.Model.Grid<SquatterList.Result>("squatter.web.list", "squatter.list",
                g => g.Column(x => x.Text));
            plugin.Panel("web.nowhere", "squatter.web.list", b => b.Query("thingId", "thingId"));
        }
    }

    [TamPlugin("gossip")]
    private sealed class UndeclaredEventPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.OnEffect<Handler>("thing-vanished");
    }

    [Fact]
    public void PLG007_and_PLG009_reject_unknown_targets()
    {
        Assert.StartsWith("PLG007", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<UnknownSlotPlugin>().Build()).Message);

        Assert.StartsWith("PLG009", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<UndeclaredEventPlugin>().Build()).Message);

        // a required field the declared event does not carry
        Assert.StartsWith("PLG009", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<GreedyEventPlugin>().Build()).Message);

        // both sides declare a kind and they disagree — the publisher owns the shape
        Assert.Contains("kinds must agree", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<KindMismatchPlugin>().Build()).Message);

        // a typo'd kind is an error at declaration, never a silent string
        Assert.Contains("unknown contract kind", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<TypoKindPlugin>().Build()).Message);

        // the docs/22 dependency line, mechanical: a plugin consuming ANOTHER PLUGIN's
        // contract fails the build on every seam — event requires, view requires, subscribes
        Assert.StartsWith("PLG010", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<NotesPlugin>().AddPlugin<EventPoacherPlugin>().Build()).Message);
        Assert.StartsWith("PLG010", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<NotesPlugin>().AddPlugin<ViewPoacherPlugin>().Build()).Message);
        Assert.StartsWith("PLG010", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<NotesPlugin>().AddPlugin<SubscriberPoacherPlugin>().Build()).Message);

        // plugins cannot declare slots — layout is the host's
        Assert.StartsWith("PLG005", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<SlotGrabbingPlugin>().Build()).Message);
    }

    [Fact]
    public void A_declared_dependency_edge_lifts_PLG010_on_the_parents_contract()
    {
        // docs/37 D-V4: the SAME consumption that poaches without an edge is legal WITH one —
        // event require, view require, and subscribe, all across the declared notes edge.
        Host().AddPlugin<NotesPlugin>().AddPlugin<DependentPlugin>().Build();
    }

    [Fact]
    public void PLG011_rejects_a_dependency_cycle()
    {
        var message = Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<CycleAPlugin>().AddPlugin<CycleBPlugin>().Build()).Message;
        Assert.StartsWith("PLG011", message);
        Assert.Contains("cycle", message);
    }

    [Fact]
    public void PLG011_rejects_a_dependency_on_an_unregistered_plugin()
    {
        Assert.StartsWith("PLG011", Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<DanglingDependencyPlugin>().Build()).Message);
    }

    [Fact]
    public void PLG011_rejects_a_self_dependency()
    {
        var message = Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<SelfDependentPlugin>().Build()).Message;
        Assert.StartsWith("PLG011", message);
        Assert.Contains("itself", message);
    }

    [TamPlugin("hungry")]
    private sealed class GreedyEventPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.RequiresEvent("thing-archived", "secretPrice");
    }

    [TamPlugin("mismatched")]
    private sealed class KindMismatchPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.RequiresEvent("thing-archived", "thingId:decimal");
    }

    [TamPlugin("typoed")]
    private sealed class TypoKindPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.RequiresEvent("thing-archived", "thingId:gui");
    }

    [TamPlugin("poacher")]
    private sealed class EventPoacherPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.RequiresEvent("notes.note-added", "noteId");
    }

    [TamPlugin("peeker")]
    private sealed class ViewPoacherPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.RequiresView("notes.for-thing", "text");
    }

    [TamPlugin("eavesdropper")]
    private sealed class SubscriberPoacherPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.OnEffect<Handler>("notes.note-added");
    }

    [TamPlugin("architect")]
    private sealed class SlotGrabbingPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.Model.Slot("architect.slot", s => s.Key("x"));
    }

    // The legal twin of the three poachers: consumes notes' contract on EVERY seam, but
    // declares the edge first — docs/37 D-V4.
    [TamPlugin("dependent")]
    private sealed class DependentPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin)
        {
            plugin.DependsOn("notes");
            plugin.RequiresEvent("notes.note-added", "noteId");
            plugin.RequiresView("notes.for-thing", "text");
            plugin.OnEffect<Handler>("notes.note-added");
        }
    }

    [TamPlugin("cyclea")]
    private sealed class CycleAPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) => plugin.DependsOn("cycleb");
    }

    [TamPlugin("cycleb")]
    private sealed class CycleBPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) => plugin.DependsOn("cyclea");
    }

    [TamPlugin("dangling")]
    private sealed class DanglingDependencyPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) => plugin.DependsOn("nonexistent");
    }

    [TamPlugin("narcissist")]
    private sealed class SelfDependentPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) => plugin.DependsOn("narcissist");
    }
}
