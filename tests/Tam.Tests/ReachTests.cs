using Tam;

namespace Tam.Tests;

// The reach seam (docs/35): the ref grammars, the REACH001 registration rules, and the
// definitions landing in the compiled model. Provider CONTAINMENT over real membership rows
// is exercised on the wire when the first consumer (the documents domain) lands.
public class ReachTests
{
    // ---- ReachRef: canonical string ↔ value ----

    [Theory]
    [InlineData("tenant", "tenant", null)]
    [InlineData("user:0d3f", "user", "0d3f")]
    [InlineData("role:dispatcher", "role", "dispatcher")]
    [InlineData("approvals.group:7a41", "approvals.group", "7a41")]
    [InlineData("role:with:colons", "role", "with:colons")]   // ids split on the FIRST colon
    public void ReachRef_parses_and_round_trips(string wire, string kind, string? id)
    {
        Assert.True(ReachRef.TryParse(wire, out var reach));
        Assert.Equal(kind, reach.Kind);
        Assert.Equal(id, reach.Id);
        Assert.Equal(wire, reach.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(":id")]          // empty kind
    [InlineData("user:")]        // trailing colon, empty id
    [InlineData("Not A Slug")]
    [InlineData("user..group:x")]
    public void ReachRef_rejects_malformed_references(string wire) =>
        Assert.False(ReachRef.TryParse(wire, out _));

    // ---- EntityRef: canonical string ↔ value ----

    [Fact]
    public void EntityRef_parses_and_round_trips()
    {
        var id = Guid.NewGuid();
        var wire = $"order:{id:D}";
        Assert.True(EntityRef.TryParse(wire, out var reference));
        Assert.Equal(new EntityRef("order", id), reference);
        Assert.Equal(wire, reference.ToString());
        Assert.Equal(reference, EntityRef.Parse(wire));
    }

    [Theory]
    [InlineData("")]
    [InlineData("order")]              // no id
    [InlineData("order:")]
    [InlineData("order:not-a-guid")]
    [InlineData(":00000000-0000-0000-0000-000000000000")]
    public void EntityRef_rejects_malformed_references(string wire)
    {
        Assert.False(EntityRef.TryParse(wire, out _));
        Assert.Throws<FormatException>(() => EntityRef.Parse(wire));
    }

    // ---- Registration: REACH001 + the compiled model ----

    private sealed class NobodyReach : IReachProvider
    {
        public Task<bool> ContainsAsync(ReachRef reach, OperationContext context, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<ReachOption>> SearchAsync(
            string? search, OperationContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReachOption>>([]);
    }

    [TamPlugin("crew")]
    private sealed class CrewPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.ReachProvider<NobodyReach>("crew.team");
    }

    [TamPlugin("crew")]
    private sealed class UnprefixedPlugin : ITamPlugin
    {
        public void Configure(PluginBuilder plugin) =>
            plugin.ReachProvider<NobodyReach>("team");
    }

    private static TamModelBuilder Host() => new TamModelBuilder()
        .LocaleDefaults("en", new Dictionary<string, string>
        {
            ["plugins.crew.title"] = "Crew",
        });

    [Fact]
    public void Host_kinds_and_plugin_kinds_land_in_the_model()
    {
        var model = Host()
            .ReachProvider<NobodyReach>("user")
            .AddPlugin<CrewPlugin>()
            .Build();

        Assert.Null(model.Reaches["user"].PluginId);
        Assert.Equal(typeof(NobodyReach), model.Reaches["user"].ProviderType);
        Assert.Equal("crew", model.Reaches["crew.team"].PluginId);
    }

    [Fact]
    public void REACH001_rejects_duplicate_kinds()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Host()
            .ReachProvider<NobodyReach>("user")
            .ReachProvider<NobodyReach>("user"));
        Assert.Contains("REACH001", ex.Message);
        Assert.Contains("twice", ex.Message);
    }

    [Fact]
    public void REACH001_rejects_a_plugin_kind_outside_its_prefix()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Host().AddPlugin<UnprefixedPlugin>().Build());
        Assert.Contains("REACH001", ex.Message);
        Assert.Contains("prefix", ex.Message);
    }

    [Fact]
    public void REACH001_rejects_malformed_kind_grammar()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Host().ReachProvider<NobodyReach>("Not A Kind"));
        Assert.Contains("REACH001", ex.Message);
    }
}

// Magic-folder bindings (docs/35): DOC001 at Build.
public class DocumentFolderTests
{
    private static TamModelBuilder Host() => new TamModelBuilder()
        .LocaleDefaults("en", new Dictionary<string, string>())
        .PublishesEvent("thing-created", "thingId", "number");

    [Fact]
    public void Binding_lands_in_the_model()
    {
        var model = Host().DocumentFolder("thing-created", "/thing/{number}").Build();
        var binding = Assert.Single(model.DocumentFolders);
        Assert.Equal("thing-created", binding.EventType);
        Assert.Equal("/thing/{number}", binding.PathTemplate);
    }

    [Fact]
    public void DOC001_rejects_an_undeclared_event()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Host().DocumentFolder("thing-deleted", "/x").Build());
        Assert.Contains("DOC001", ex.Message);
        Assert.Contains("undeclared", ex.Message);
    }

    [Fact]
    public void DOC001_rejects_a_placeholder_outside_the_payload_contract()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Host().DocumentFolder("thing-created", "/thing/{title}").Build());
        Assert.Contains("DOC001", ex.Message);
        Assert.Contains("{title}", ex.Message);
    }
}
