using System.Text.Json;
using Tam;
using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// The tam.rules rules.schema view is FRAMEWORK behavior: the server-authoritative answer to "what
/// compiled ROW fields can a rule on this trigger reference, and with what types" — the one thing the
/// manifest cannot supply. Exercised through the framework-owned bins.close trigger (target row = bin).
/// </summary>
public sealed class RuleSchemaTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;
    private TestActor<WidgetDbContext> admin = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());
        admin = host.Actor("demo", "rules.manage");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private static string Str(Dictionary<string, JsonElement> row, string key) => row[key].GetString() ?? "";

    private static Dictionary<string, JsonElement> Row(object raw) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(raw, TamJson.Options), TamJson.Options)!;

    [Fact]
    public async Task Operation_with_a_target_row_lists_that_row_s_compiled_fields_typed()
    {
        // bins.close carries binId → target row = bin. The builder gets the bin's scalar fields, each
        // typed exactly as the manifest types operation fields.
        var (response, error) = await admin.QueryAsync("rules.schema", new Dictionary<string, string?>
        {
            ["trigger"] = "bins.close",
            ["kind"] = "operation",
            ["pageSize"] = "200",
        });
        Assert.Null(error);
        var rows = response!.Rows.Select(Row).ToList();

        Assert.All(rows, r => Assert.Equal("bin", Str(r, "entityKey")));

        // status is an enum → wireKind string carrying its options; budget is money → number.
        var status = rows.Single(r => Str(r, "path") == "row.status");
        Assert.Equal("string", Str(status, "wireKind"));
        Assert.NotEmpty(status["options"].EnumerateArray());

        var budget = rows.Single(r => Str(r, "path") == "row.budget");
        Assert.Equal("number", Str(budget, "wireKind"));

        // The extension bag never JSON-serializes to a comparable value, so it is not offered.
        Assert.DoesNotContain(rows, r => Str(r, "path") == "row.extensions");
    }

    [Fact]
    public async Task Trigger_without_a_single_target_row_offers_no_row_fields()
    {
        // A create carrying no {entity}Id of its own → RUL004: no row.* namespace, so the schema is empty.
        var (response, error) = await admin.QueryAsync("rules.schema", new Dictionary<string, string?>
        {
            ["trigger"] = "widgets.create-plain",
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

    [Fact]
    public async Task Rules_list_carries_the_full_definition_for_the_edit_row_form()
    {
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "editable",
            onOperation = "bins.close",
            condition = """{"t":"bin","op":"gt","l":{"t":"field","f":"row.budget"},"r":{"t":"const","v":5}}""",
            messages = new Dictionary<string, string> { ["sv"] = "Stopp", ["en"] = "Stop" },
        })).ShouldSucceed();

        var (response, error) = await admin.QueryAsync("rules.list");
        Assert.Null(error);
        var row = Row(response!.Rows.Single(r => Row(r)["name"].GetString() == "editable"));
        Assert.Contains("row.budget", Str(row, "condition"));
        Assert.Equal("Stopp", row["messages"].GetProperty("sv").GetString());
        Assert.False(row.ContainsKey("onEvent"));   // null → omitted on the wire (TamJson)

        // The round trip: prefill → change → re-define under the same name updates in place.
        (await admin.ExecuteAsync("rules.define", new
        {
            name = "editable",
            onOperation = "bins.close",
            condition = """{"t":"bin","op":"gt","l":{"t":"field","f":"row.budget"},"r":{"t":"const","v":9}}""",
            messages = new Dictionary<string, string> { ["sv"] = "Stopp", ["en"] = "Stop" },
        })).ShouldSucceed();
        var (after, _) = await admin.QueryAsync("rules.list");
        var edited = Row(after!.Rows.Single(r => Row(r)["name"].GetString() == "editable"));
        Assert.Contains("\"v\":9", Str(edited, "condition").Replace(" ", ""));
        Assert.Equal(1, after.Rows.Count(r => Row(r)["name"].GetString() == "editable"));
    }
}
