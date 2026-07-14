using System.Text.Json;

namespace Tam;

/// <summary>
/// Framework-managed container for tenant-defined field values (docs/15). Not a free-form bag:
/// writes flow through the pipeline, which validates against the tenant field registry.
/// Stored as one JSON column on the extensible aggregate.
/// </summary>
public sealed class ExtensionData
{
    private readonly Dictionary<string, JsonElement> values;

    public ExtensionData() : this(new Dictionary<string, JsonElement>()) { }

    private ExtensionData(Dictionary<string, JsonElement> values) => this.values = values;

    public IReadOnlyDictionary<string, JsonElement> Values => values;

    public T? Get<T>(string key) =>
        values.TryGetValue(key, out var element) && element.ValueKind != JsonValueKind.Null
            ? element.Deserialize<T>(TamJson.Options)
            : default;

    public object? Raw(string key) =>
        values.TryGetValue(key, out var e) ? FromElement(e) : null;

    /// <summary>Framework-internal write path; application code goes through operations.</summary>
    public ExtensionData WithValue(string key, object? value)
    {
        var next = new Dictionary<string, JsonElement>(values)
        {
            [key] = JsonSerializer.SerializeToElement(value, TamJson.Options),
        };
        return new ExtensionData(next);
    }

    public string ToJson() => JsonSerializer.Serialize(values, TamJson.Options);

    public static ExtensionData FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new ExtensionData()
            : new ExtensionData(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!) ?? []);

    public static object? FromElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.ToString(),
    };
}

public interface IExtensible
{
    ExtensionData Extensions { get; set; }
}

/// <summary>On the wire, <see cref="ExtensionData"/> is a plain key/value object.</summary>
public sealed class ExtensionDataJsonConverter : System.Text.Json.Serialization.JsonConverter<ExtensionData>
{
    public override ExtensionData Read(
        ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
        return ExtensionData.FromJson(doc.RootElement.GetRawText());
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer, ExtensionData value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, element) in value.Values)
        {
            writer.WritePropertyName(key);
            element.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}

/// <summary>Registry access as the pipeline and manifest see it (implementation in Tam.EntityFrameworkCore).</summary>
public interface IExtensionRegistry
{
    Task<IReadOnlyList<ExtensionFieldSpec>> For(TenantId tenant, string entityKey, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ExtensionFieldSpec>>> All(TenantId tenant, CancellationToken ct);
}

public enum ExtensionFieldState
{
    Draft,
    Active,
    Deprecated,
    Retired,
}

/// <summary>A tenant-defined field, as the runtime model consumes it (registry storage lives in EF).</summary>
public sealed record ExtensionFieldSpec(
    string Key,
    string Entity,
    string Type,
    bool Required,
    int? MaxLength,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string>? Descriptions,
    IReadOnlyList<string>? Options,
    ExtensionFieldState State)
{
    public SemanticType Semantic
    {
        get
        {
            var semantic = SemanticTypes.ByKey.GetValueOrDefault(Type, SemanticTypes.Text);
            if (MaxLength is not { } max) return semantic;
            var prior = semantic.Validate;
            return semantic with
            {
                MaxLength = max,
                Validate = v => v is string s && s.Length > max
                    ? ValidationFindings.TooLong.With(("max", max))
                    : prior(v),
            };
        }
    }
}

public static class ExtensionFindings
{
    public static readonly FindingFactory UnknownField = Finding.Error("extensions.unknown-field");
    public static readonly FindingFactory RetiredField = Finding.Error("extensions.retired-field");
    public static readonly FindingFactory DeprecatedField = Finding.Warning("extensions.deprecated-field");
    public static readonly FindingFactory UnknownType = Finding.Error("extensions.unknown-type");
    public static readonly FindingFactory KeyConflict = Finding.Error("extensions.key-conflict");
    public static readonly FindingFactory MissingLabel = Finding.Error("extensions.missing-label");
    public static readonly FindingFactory InvalidOption = Finding.Error("extensions.invalid-option");
}
