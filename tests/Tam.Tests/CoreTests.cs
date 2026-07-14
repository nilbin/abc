using System.Text.Json;
using Tam;

namespace Tam.Tests;

public class ChangeJsonTests
{
    private sealed record EditInput(
        string Id,
        Change<string?>? Description = null,
        Change<DateOnly?>? RequestedDate = null);

    [Fact]
    public void Absent_property_means_untouched_and_null_value_means_clear()
    {
        var json = """{"id":"x","description":{"original":"a","value":null}}""";
        var input = JsonSerializer.Deserialize<EditInput>(json, TamJson.Options)!;

        Assert.NotNull(input.Description);          // present → touched
        Assert.Null(input.Description!.Value);       // value null → explicit clear
        Assert.Equal("a", input.Description.Original);
        Assert.Null(input.RequestedDate);            // absent → untouched
    }

    [Fact]
    public void Value_wrappers_serialize_as_their_underlying_primitive()
    {
        var json = JsonSerializer.Serialize(new Erp.OrderDescription("hello"), TamJson.Options);
        Assert.Equal("\"hello\"", json);

        var back = JsonSerializer.Deserialize<Erp.OrderDescription>("\"hi\"", TamJson.Options);
        Assert.Equal("hi", back.Value);
    }
}

public class PortableExpressionTests
{
    private sealed record Input(Erp.OrderType OrderType, string? Note, decimal? Total);

    [Fact]
    public void Enum_comparison_lowers_to_name_constants_and_evaluates()
    {
        var px = PortableExpression.Lower<Input>(x => x.OrderType == Erp.OrderType.Project);

        var whenProject = px.Evaluate(f => f == "orderType" ? "Project" : null);
        var whenService = px.Evaluate(f => f == "orderType" ? "Service" : null);

        Assert.Equal(true, whenProject);
        Assert.Equal(false, whenService);
    }

    [Fact]
    public void Compound_and_null_checks_work()
    {
        var px = PortableExpression.Lower<Input>(x => x.OrderType == Erp.OrderType.Project && x.Total > 100);
        Assert.Equal(true, px.Evaluate(f => f switch
        {
            "orderType" => "Project",
            "total" => 250m,
            _ => null,
        }));
        Assert.Equal(false, px.Evaluate(f => f switch
        {
            "orderType" => "Project",
            "total" => null,
            _ => null,
        }));
    }

    [Fact]
    public void Nodes_outside_the_subset_fail_loudly_not_partially()
    {
        Assert.Throws<NotSupportedException>(() =>
            PortableExpression.Lower<Input>(x => x.Note!.Contains("x")));
    }

    [Fact]
    public void Ast_round_trips_through_json()
    {
        var px = PortableExpression.Lower<Input>(x => x.OrderType == Erp.OrderType.Project);
        var json = JsonSerializer.Serialize(px, TamJson.Options);
        var back = JsonSerializer.Deserialize<Px>(json, TamJson.Options)!;
        Assert.Equal(true, back.Evaluate(f => "Project"));
    }
}

public class LocalizationTests
{
    private static LocaleCatalogs Catalogs()
    {
        var catalogs = new LocaleCatalogs("sv");
        catalogs.Add("sv", new Dictionary<string, string>
        {
            ["validation.too-long"] = "Får innehålla högst {max} tecken.",
            ["labels.name"] = "Namn",
        });
        catalogs.Add("en", new Dictionary<string, string>
        {
            ["validation.too-long"] = "Must be at most {max} characters.",
        });
        return catalogs;
    }

    [Fact]
    public void Message_resolves_in_request_culture_with_args()
    {
        var finding = ValidationFindings.TooLong.With(("max", 40));
        var sv = Catalogs().Resolve(finding, "sv");
        var en = Catalogs().Resolve(finding, "en");

        Assert.Equal("Får innehålla högst 40 tecken.", sv.Message);
        Assert.Equal("Must be at most 40 characters.", en.Message);
    }

    [Fact]
    public void Missing_culture_falls_back_to_default_and_missing_keys_are_reported()
    {
        var catalogs = Catalogs();
        Assert.Equal("Namn", catalogs.Lookup("labels.name", "en"));   // en → sv fallback
        var missing = catalogs.MissingKeys(["labels.name", "labels.missing"], "sv");
        Assert.Equal(["labels.missing"], missing);
    }
}
