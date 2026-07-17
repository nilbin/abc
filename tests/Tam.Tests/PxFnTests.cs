using System.Text.Json;
using Tam;

namespace Tam.Tests;

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
