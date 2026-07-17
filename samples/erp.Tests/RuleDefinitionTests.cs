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
}
