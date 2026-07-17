using System.Text.Json;
using Erp;
using Tam;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// docs/22 rule-builder schema: the rules.schema view is the server-authoritative answer to
/// "what compiled ROW fields can a rule on this trigger reference, and with what types" — the
/// one thing the manifest cannot supply. The visual builder renders value controls from it.
/// </summary>
public sealed class RuleSchemaTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private TestActor<ErpDbContext> admin = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        admin = host.Actor("demo", "rules.manage");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private static string Str(Dictionary<string, JsonElement> row, string key) =>
        row[key].GetString() ?? "";

    private static Dictionary<string, JsonElement> Row(object raw) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(raw, TamJson.Options), TamJson.Options)!;

    [Fact]
    public async Task Operation_with_a_target_row_lists_that_row_s_compiled_fields_typed()
    {
        // projects.close carries projectId → target row = project. The builder gets the project's
        // scalar fields, each typed exactly as the manifest types operation fields.
        var (response, error) = await admin.QueryAsync("rules.schema", new Dictionary<string, string?>
        {
            ["trigger"] = "projects.close",
            ["kind"] = "operation",
            ["pageSize"] = "200",
        });
        Assert.Null(error);
        var rows = response!.Rows.Select(Row).ToList();

        // Every row names the target entity so the client can pull its extension fields.
        Assert.All(rows, r => Assert.Equal("project", Str(r, "entityKey")));

        // status is an enum → wireKind string carrying its options; budget is money → number.
        var status = rows.Single(r => Str(r, "path") == "row.status");
        Assert.Equal("string", Str(status, "wireKind"));
        Assert.NotEmpty(status["options"].EnumerateArray());

        var budget = rows.Single(r => Str(r, "path") == "row.budget");
        Assert.Equal("number", Str(budget, "wireKind"));

        // Navigations / collections / the extension bag never JSON-serialize to a comparable
        // value, so they are not offered as references.
        Assert.DoesNotContain(rows, r => Str(r, "path") == "row.extensions");
    }

    [Fact]
    public async Task Trigger_without_a_single_target_row_offers_no_row_fields()
    {
        // A create carries no {entity}Id → RUL004: no row.* namespace, so the schema is empty
        // (the builder then offers only the operation's own input fields, from the manifest).
        var (response, error) = await admin.QueryAsync("rules.schema", new Dictionary<string, string?>
        {
            ["trigger"] = "orders.create",
            ["kind"] = "operation",
        });
        Assert.Null(error);
        Assert.Empty(response!.Rows);
    }

    [Fact]
    public async Task An_unknown_trigger_is_empty_never_an_error()
    {
        var (response, error) = await admin.QueryAsync("rules.schema", new Dictionary<string, string?>
        {
            ["trigger"] = "nope.not-a-thing",
            ["kind"] = "operation",
        });
        Assert.Null(error);
        Assert.Empty(response!.Rows);
    }
}
