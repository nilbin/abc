using Erp;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>RTFM #3 regressions: a hand-authored condition that gets the Px wire shape wrong
/// must come back as a FINDING naming the problem — never a 500 — and an unsupported operator
/// must be named, not left to trial and error.</summary>
public sealed class RuleDefinitionTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private TestActor<ErpDbContext> admin = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        admin = host.Actor("demo", "rules.manage");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Wrong_wire_shape_is_a_finding_not_an_exception()
    {
        // The docs' old sketch shape — no "t" discriminator. STJ throws NotSupportedException;
        // the operation must translate that to rules.invalid-condition.
        var response = await admin.ExecuteAsync("rules.define", new
        {
            name = "bad-shape",
            onOperation = "orders.complete",
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
            onOperation = "orders.complete",
            condition = """{"t":"bin","op":"gte","l":{"t":"field","f":"orderId"},"r":{"t":"const","v":1}}""",
            messages = new Dictionary<string, string> { ["sv"] = "x", ["en"] = "x" },
        });
        response.ShouldFailWith("rules.invalid-condition");
        var finding = response.Findings.Single(f => f.Code == "rules.invalid-condition");
        Assert.Equal("gte", finding.Args.GetValueOrDefault("op")?.ToString());
    }

    [Fact]
    public async Task Comparing_against_the_null_constant_is_refused_toward_isNull()
    {
        // The builder's unfinished-clause shape: eq(field, null) means "field is empty", which
        // is almost never what the author intended — isNull/isNotNull exist for that. Refuse
        // at define rather than store a rule that silently means something else.
        var response = await admin.ExecuteAsync("rules.define", new
        {
            name = "null-compare",
            onOperation = "orders.complete",
            condition = """{"t":"bin","op":"eq","l":{"t":"field","f":"orderId"},"r":{"t":"const","v":null}}""",
            messages = new Dictionary<string, string> { ["sv"] = "x", ["en"] = "x" },
        });
        response.ShouldFailWith("rules.invalid-condition", onField: "condition");

        // The explicit presence operators remain the sanctioned spelling.
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "is-null-ok",
            onOperation = "orders.complete",
            condition = """{"t":"un","op":"isNull","x":{"t":"field","f":"orderId"}}""",
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
            onOperation = "orders.complete",
            condition = """{"t":"const","v":true}""",
            action = """{"type":"publish-event"}""",
        })).ShouldSucceed();

        // ...but a FINDING rule (no action) demands a message, and RUL003 is the AUTHORITATIVE
        // guard (docs/40 — the operation's own domain rule, not a form scan). It is richer than
        // mere presence: it wants the DEFAULT culture ("sv"). A direct operation call (no form
        // binding) is bound by exactly this rule, so an entirely-omitted map...
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "silent-finding",
            onOperation = "orders.complete",
            condition = """{"t":"const","v":true}""",
        })).ShouldFailWith("rules.missing-message", onField: "messages");

        // ...and a non-empty map that still lacks the default culture both fail the SAME domain
        // rule with the SAME precise finding — no generic validation.required from a form scan.
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "wrong-culture-finding",
            onOperation = "orders.complete",
            condition = """{"t":"const","v":true}""",
            messages = new Dictionary<string, string> { ["en"] = "x" },
        })).ShouldFailWith("rules.missing-message", onField: "messages");
    }
}
