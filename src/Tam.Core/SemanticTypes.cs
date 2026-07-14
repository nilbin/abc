using System.Reflection;

namespace Tam;

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property)]
public sealed class MultilineAttribute : Attribute;

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property)]
public sealed class FormatAttribute(string format) : Attribute
{
    public string Format { get; } = format;
}

/// <summary>Intrinsic maximum length of a semantic text type; verified against persistence (DB001).</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property)]
public sealed class MaxLengthAttribute(int length) : Attribute
{
    public int Length { get; } = length;
}

/// <summary>Overrides the convention-derived label key. Never carries display text (L10N000).</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class LabelKeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

/// <summary>
/// The closed vocabulary shared by compiled fields and tenant extension fields
/// (docs/02, docs/15). One implementation per key: wire kind, format, normalization,
/// semantic equality — reused everywhere the type appears.
/// </summary>
public sealed record SemanticType(
    string Key,
    string WireKind,          // "string" | "number" | "integer" | "boolean" | "date" | "datetime" | "object"
    string? Format = null,    // "email" | "phone" | "url" | "multiline" | "money" | "reference:<entity>" | null
    int? MaxLength = null)
{
    /// <summary>Adds an intrinsic max length, stacking on any existing validation.</summary>
    public SemanticType WithMaxLength(int max)
    {
        var prior = Validate;
        return this with
        {
            MaxLength = max,
            Validate = v => v is string s && s.Length > max
                ? ValidationFindings.TooLong.With(("max", max))
                : prior(v),
        };
    }

    /// <summary>JSON Schema type for a wire kind — single mapping for OpenAPI and MCP.</summary>
    public static string JsonType(string wireKind) => wireKind switch
    {
        "number" => "number",
        "integer" => "integer",
        "boolean" => "boolean",
        "object" => "object",
        _ => "string",
    };

    public Func<object?, object?> Normalize { get; init; } = v => v;

    /// <summary>Semantic equality on normalized values — drives dirty tracking and the three-way merge.</summary>
    public Func<object?, object?, bool> SemanticEquals { get; init; } =
        (a, b) => Equals(a, b);

    public Func<object?, Finding?> Validate { get; init; } = _ => null;
}

public static class ValidationFindings
{
    public static readonly FindingFactory TooLong = Finding.Error("validation.too-long");
    public static readonly FindingFactory InvalidEmail = Finding.Error("validation.invalid-email");
    public static readonly FindingFactory Required = Finding.Error("validation.required");
    public static readonly FindingFactory UnknownField = Finding.Error("validation.unknown-field");
    public static readonly FindingFactory InvalidValue = Finding.Error("validation.invalid-value");
}

/// <summary>Maps CLR types (including single-value wrapper record structs) to semantic types.</summary>
public static class SemanticTypes
{
    public static readonly SemanticType Text = new("text", "string")
    {
        Normalize = v => (v as string)?.Trim(),
    };

    public static readonly SemanticType MultilineText = new("multiline-text", "string", "multiline")
    {
        Normalize = v => (v as string)?.Trim().ReplaceLineEndings("\n"),
    };

    public static readonly SemanticType Email = new("email", "string", "email")
    {
        Normalize = v => (v as string)?.Trim().ToLowerInvariant(),
        Validate = v => v is string s && s.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(s, out _)
            ? ValidationFindings.InvalidEmail.Create()
            : null,
    };

    public static readonly SemanticType Phone = new("phone", "string", "phone")
    {
        Normalize = v => v is string s
            ? new string(s.Where(c => char.IsDigit(c) || c == '+').ToArray())
            : v,
    };

    public static readonly SemanticType Number = new("number", "number");
    public static readonly SemanticType Integer = new("integer", "integer");

    public static readonly SemanticType Money = new("money", "number", "money")
    {
        SemanticEquals = (a, b) => a is decimal da && b is decimal db ? decimal.Compare(
            decimal.Round(da, 2), decimal.Round(db, 2)) == 0 : Equals(a, b),
    };

    public static readonly SemanticType Bool = new("boolean", "boolean");
    public static readonly SemanticType Date = new("date", "date");
    public static readonly SemanticType DateTime = new("datetime", "datetime");
    public static readonly SemanticType Selection = new("selection", "string");
    public static readonly SemanticType Reference = new("reference", "string");
    public static readonly SemanticType Complex = new("object", "object");

    /// <summary>Runtime-definable types for tenant custom fields, by registry key.</summary>
    public static readonly IReadOnlyDictionary<string, SemanticType> ByKey =
        new Dictionary<string, SemanticType>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = Text,
            ["multiline-text"] = MultilineText,
            ["email"] = Email,
            ["phone"] = Phone,
            ["number"] = Number,
            ["integer"] = Integer,
            ["money"] = Money,
            ["boolean"] = Bool,
            ["date"] = Date,
            ["datetime"] = DateTime,
            ["selection"] = Selection,
            ["reference"] = Reference,
        };

    public static SemanticType For(Type clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        var inner = ValueWrapper.UnderlyingType(t) ?? t;
        var format = t.GetCustomAttribute<FormatAttribute>()?.Format;
        var multiline = t.GetCustomAttribute<MultilineAttribute>() is not null;
        var maxLength = t.GetCustomAttribute<MaxLengthAttribute>()?.Length;

        var baseType = format switch
        {
            "email" => Email,
            "phone" => Phone,
            _ when multiline => MultilineText,
            _ when inner == typeof(string) => Text,
            _ when inner == typeof(decimal) => t.Name.Contains("Money", StringComparison.OrdinalIgnoreCase) ? Money : Number,
            _ when inner == typeof(int) || inner == typeof(long) => Integer,
            _ when inner == typeof(bool) => Bool,
            _ when inner == typeof(DateOnly) => Date,
            _ when inner == typeof(System.DateTime) || inner == typeof(DateTimeOffset) => DateTime,
            _ when inner == typeof(Guid) => Reference,
            _ when t.IsEnum => Selection,
            _ => Complex,
        };

        return maxLength is { } max ? baseType.WithMaxLength(max) : baseType;
    }
}
