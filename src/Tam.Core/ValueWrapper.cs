using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tam;

/// <summary>
/// Support for single-value semantic wrapper types: <c>readonly record struct EmailAddress(string Value)</c>,
/// <c>readonly record struct CustomerId(Guid Value)</c>. On the wire they are their underlying primitive.
/// </summary>
public static class ValueWrapper
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> Cache = new();

    public static PropertyInfo? ValueProperty(Type type)
        => Cache.GetOrAdd(type, static t =>
        {
            // Nullable<T> has a single "value" ctor parameter too — it is not a semantic wrapper;
            // serializers unwrap it first and then hit the underlying wrapper converter.
            if (Nullable.GetUnderlyingType(t) is not null) return null;
            if (!t.IsValueType && t != typeof(string) && !t.IsClass) return null;
            var ctor = t.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 1
                && string.Equals(c.GetParameters()[0].Name, "Value", StringComparison.OrdinalIgnoreCase));
            if (ctor is null) return null;
            return t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        });

    public static Type? UnderlyingType(Type type) => ValueProperty(type)?.PropertyType;

    public static bool IsWrapper(Type type) => ValueProperty(type) is not null;

    public static object? Unwrap(object? value)
    {
        if (value is null) return null;
        var prop = ValueProperty(value.GetType());
        return prop is null ? value : prop.GetValue(value);
    }

    public static object Wrap(Type wrapperType, object? underlying)
        => Activator.CreateInstance(wrapperType, underlying)!;
}

/// <summary>Serializes wrapper types as their underlying value; applies to any single-"Value"-ctor type.</summary>
public sealed class ValueWrapperJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => ValueWrapper.IsWrapper(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var underlying = ValueWrapper.UnderlyingType(typeToConvert)!;
        return (JsonConverter)Activator.CreateInstance(
            typeof(Converter<,>).MakeGenericType(typeToConvert, underlying))!;
    }

    private sealed class Converter<TWrapper, TValue> : JsonConverter<TWrapper>
    {
        public override TWrapper Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
            return (TWrapper)ValueWrapper.Wrap(typeof(TWrapper), value);
        }

        public override void Write(Utf8JsonWriter writer, TWrapper value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, (TValue?)ValueWrapper.Unwrap(value), options);
    }
}

public static class TamJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new ValueWrapperJsonConverterFactory());
        options.Converters.Add(new ExtensionDataJsonConverter());
        return options;
    }
}
