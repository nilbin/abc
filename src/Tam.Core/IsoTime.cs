using System.Globalization;

namespace Tam;

/// <summary>
/// The ONE ISO-8601 encoding for the framework's *_Iso string columns: round-trip ("O") format,
/// normalized to UTC. UTC matters beyond convention — it is what makes STRING ordering equal
/// CHRONOLOGICAL ordering, the property every lease/backoff/retention query relies on when it
/// compares an Iso column against a cutoff with string.Compare. Parsing is invariant-culture,
/// round-trip-kind; never call bare DateTimeOffset.Parse on an Iso column (culture-sensitive,
/// and it silently accepts non-Iso shapes).
/// </summary>
public static class IsoTime
{
    public static string Now() => From(DateTimeOffset.UtcNow);

    public static string From(DateTimeOffset instant) => instant.ToUniversalTime().ToString("O");

    public static bool TryParse(string? iso, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
}
