using System.Text.Json;
using Tam;

namespace Tam.Tests;

/// <summary>Review round 5, F2: a tenant-authored PxConst deserializes to a JsonElement, and
/// Truthy must normalize it — otherwise a const/field in a boolean position silently evaluated
/// false server-side while the client fired. These pin the two evaluators together.</summary>
public class PxTruthyTests
{
    private static Px Parse(string json) => JsonSerializer.Deserialize<Px>(json, TamJson.Options)!;

    [Fact]
    public void A_bare_true_const_is_truthy()
    {
        Assert.True(PxBinary.Truthy(Parse("""{"t":"const","v":true}""").Evaluate(_ => null)));
        Assert.False(PxBinary.Truthy(Parse("""{"t":"const","v":false}""").Evaluate(_ => null)));
    }

    [Fact]
    public void And_of_a_const_and_a_comparison_fires()
    {
        // and(const true, eq(field, 1)) — the const operand must not drag the whole thing false.
        var px = Parse("""
            {"t":"bin","op":"and",
             "l":{"t":"const","v":true},
             "r":{"t":"bin","op":"eq","l":{"t":"field","f":"x"},"r":{"t":"const","v":1}}}
            """);
        Assert.True(PxBinary.Truthy(px.Evaluate(n => n == "x" ? 1m : null)));
    }

    [Fact]
    public void Not_of_a_false_const_is_true()
    {
        Assert.True(PxBinary.Truthy(Parse("""{"t":"un","op":"not","x":{"t":"const","v":false}}""")
            .Evaluate(_ => null)));
    }
}

/// <summary>The relative-date Px node (docs/22, RTFM #3's find): {"t":"fn","op":"today","days":N}
/// evaluates fresh on every check — the policy never drifts the way a define-time constant does.</summary>
public class PxFnTests
{
    [Fact]
    public void Today_plus_offset_evaluates_to_the_iso_date()
    {
        PxFn.Today = () => new DateOnly(2026, 7, 17);
        try
        {
            var px = JsonSerializer.Deserialize<Px>(
                """{"t":"fn","op":"today","days":7}""", TamJson.Options)!;
            Assert.Equal("2026-07-24", px.Evaluate(_ => null));
        }
        finally { PxFn.Today = null; }
    }

    [Fact]
    public void A_date_window_condition_flips_with_the_clock_not_the_definition()
    {
        // "scheduledDate more than 7 days out" — same stored rule, two different days.
        var condition = JsonSerializer.Deserialize<Px>(
            """{"t":"bin","op":"gt","l":{"t":"field","f":"scheduledDate"},"r":{"t":"fn","op":"today","days":7}}""",
            TamJson.Options)!;
        object? Field(string name) => name == "scheduledDate" ? "2026-07-30" : null;

        try
        {
            PxFn.Today = () => new DateOnly(2026, 7, 17);   // cutoff 07-24 → 07-30 is too far
            Assert.Equal(true, condition.Evaluate(Field));

            PxFn.Today = () => new DateOnly(2026, 7, 25);   // cutoff 08-01 → 07-30 is fine now
            Assert.Equal(false, condition.Evaluate(Field));
        }
        finally { PxFn.Today = null; }
    }
}
