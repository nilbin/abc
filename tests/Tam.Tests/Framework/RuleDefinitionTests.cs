using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// The tam.rules package's define-time validation is FRAMEWORK behavior: a hand-authored condition that
/// gets the Px wire shape wrong must come back as a FINDING naming the problem — never a 500 — and an
/// unsupported operator must be named. Exercised through the framework-owned bins.close trigger.
/// </summary>
public sealed class RuleDefinitionTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;
    private TestActor<WidgetDbContext> admin = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());
        admin = host.Actor("demo", "rules.manage");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Wrong_wire_shape_is_a_finding_not_an_exception()
    {
        var response = await admin.ExecuteAsync("rules.define", new
        {
            name = "bad-shape",
            onOperation = "bins.close",
            condition = """{"bin":"and","l":{"field":"x"},"r":{"const":1}}""",
            messages = new Dictionary<string, string> { ["sv"] = "x", ["en"] = "x" },
        });
        response.ShouldFailWith("rules.invalid-condition", onField: "condition");
    }

    [Fact]
    public async Task Unsupported_operator_is_named_in_the_finding()
    {
        var response = await admin.ExecuteAsync("rules.define", new
        {
            name = "bad-op",
            onOperation = "bins.close",
            condition = """{"t":"bin","op":"gte","l":{"t":"field","f":"binId"},"r":{"t":"const","v":1}}""",
            messages = new Dictionary<string, string> { ["sv"] = "x", ["en"] = "x" },
        });
        response.ShouldFailWith("rules.invalid-condition");
        var finding = response.Findings.Single(f => f.Code == "rules.invalid-condition");
        Assert.Equal("gte", finding.Args.GetValueOrDefault("op")?.ToString());
    }

    [Fact]
    public async Task Comparing_against_the_null_constant_is_refused_toward_isNull()
    {
        var response = await admin.ExecuteAsync("rules.define", new
        {
            name = "null-compare",
            onOperation = "bins.close",
            condition = """{"t":"bin","op":"eq","l":{"t":"field","f":"binId"},"r":{"t":"const","v":null}}""",
            messages = new Dictionary<string, string> { ["sv"] = "x", ["en"] = "x" },
        });
        response.ShouldFailWith("rules.invalid-condition", onField: "condition");

        (await admin.ExecuteAsync("rules.define", new
        {
            name = "is-null-ok",
            onOperation = "bins.close",
            condition = """{"t":"un","op":"isNull","x":{"t":"field","f":"binId"}}""",
            messages = new Dictionary<string, string> { ["sv"] = "x", ["en"] = "x" },
        })).ShouldSucceed();
    }

    [Fact]
    public async Task Messages_are_optional_for_action_rules_but_required_for_finding_rules()
    {
        // No messages at all: fine for an action rule (nothing blocks, no text to show)...
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "quiet-action",
            onOperation = "bins.close",
            condition = """{"t":"const","v":true}""",
            action = """{"type":"publish-event"}""",
        })).ShouldSucceed();

        // ...but a FINDING rule (no action) demands a message, and RUL003 is the AUTHORITATIVE guard: it
        // wants the DEFAULT culture ("en"). An entirely-omitted map fails...
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "silent-finding",
            onOperation = "bins.close",
            condition = """{"t":"const","v":true}""",
        })).ShouldFailWith("rules.missing-message", onField: "messages");

        // ...and a non-empty map that still lacks the default culture fails the SAME domain rule.
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "wrong-culture-finding",
            onOperation = "bins.close",
            condition = """{"t":"const","v":true}""",
            messages = new Dictionary<string, string> { ["sv"] = "x" },
        })).ShouldFailWith("rules.missing-message", onField: "messages");
    }
}
