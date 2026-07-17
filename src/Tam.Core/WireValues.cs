using System.Text.Json;

namespace Tam;

/// <summary>
/// Typed accessors over WIRE JSON — the values a plugin reads from gate inputs, effect payloads
/// and host view rows (docs/22 P2, docs/31 D-X3). One rule everywhere: a missing property, a
/// JSON null, or a mismatched kind reads as <c>null</c>, never throws — a partner's incomplete
/// row or a contract that grew must degrade to a finding or a skip downstream, not a 500. The
/// nullable return IS the "was it there" answer; default with <c>?? ""</c> / <c>?? 0m</c> at
/// the call site when absence is benign.
/// </summary>
public static class WireValues
{
    /// <summary>A view result row as wire JSON — the shape the manifest promised (camel names,
    /// wrapped values unwrapped), so reads match the DECLARED field list, not CLR reflection.</summary>
    public static JsonElement Row(object row) =>
        JsonSerializer.SerializeToElement(row, TamJson.Options);

    /// <summary>The response's first row as wire JSON, or null when empty — the common
    /// "look up one record" shape over <c>IHostViewReader</c>.</summary>
    public static JsonElement? FirstRow(this ViewResponse response) =>
        response.Rows.Count == 0 ? null : Row(response.Rows[0]);

    /// <summary>Every row as wire JSON — for aggregation over a declared read.</summary>
    public static IEnumerable<JsonElement> WireRows(this ViewResponse response) =>
        response.Rows.Select(Row);

    public static string? String(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() : null;

    public static Guid? Guid(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value
            && value.TryGetGuid(out var guid) ? guid : null;

    public static decimal? Decimal(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value
            ? value.GetDecimal() : null;

    public static int? Int(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value
            && value.TryGetInt32(out var i) ? i : null;

    public static bool? Bool(this JsonElement element, string name) =>
        Property(element, name) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value) ? value : null;
}
