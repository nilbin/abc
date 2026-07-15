using System.Reflection;

namespace Tam;

public sealed record FieldModel(
    string MemberName,
    string WireName,
    Type ClrType,
    Type EffectiveType,          // Change<T> unwrapped
    bool IsChangeSet,
    bool Required,
    SemanticType Semantic,
    string LabelKey,
    IReadOnlyList<string>? EnumOptions,
    string? SensitivePermission = null)   // docs/27 D-A3: field masked behind this atom
{
    /// <summary>docs/27 D-A3: is this field hidden/blocked for the actor? The ONE predicate the
    /// operation and view masking sites share, so they can never disagree.</summary>
    public bool IsMaskedFor(Actor actor) =>
        SensitivePermission is { } atom && !actor.Can(atom);

    // Record positional params surface [property:]-targeted attributes on the generated property,
    // so the parameter overload searches both; the property overload has one source.
    public static FieldModel From(ParameterInfo parameter, NullabilityInfoContext nullability) =>
        Build(parameter.Name!, parameter.ParameterType,
            hasDefault: parameter.HasDefaultValue,
            writeState: nullability.Create(parameter).WriteState,
            attributeSources: [parameter, parameter.Member.DeclaringType?.GetProperty(parameter.Name!)]);

    public static FieldModel From(PropertyInfo property, NullabilityInfoContext nullability) =>
        Build(property.Name, property.PropertyType,
            hasDefault: false,
            writeState: nullability.Create(property).WriteState,
            attributeSources: [property]);

    private static FieldModel Build(
        string name, Type clr, bool hasDefault, NullabilityState writeState,
        ICustomAttributeProvider?[] attributeSources)
    {
        var isChange = clr.IsGenericType && clr.GetGenericTypeDefinition() == typeof(Change<>);
        var effective = isChange ? clr.GetGenericArguments()[0] : clr;
        var nonNullable = Nullable.GetUnderlyingType(effective) ?? effective;

        var required = !isChange
            && !hasDefault
            && Nullable.GetUnderlyingType(effective) is null
            && (effective.IsValueType || writeState == NullabilityState.NotNull);

        var labelKey = Attr<LabelKeyAttribute>(attributeSources)?.Key
            ?? nonNullable.GetCustomAttribute<LabelKeyAttribute>()?.Key
            ?? $"labels.{Naming.Kebab(name)}";

        return new FieldModel(
            name,
            Naming.Camel(name),
            clr,
            effective,
            isChange,
            required,
            SemanticTypes.For(nonNullable),
            labelKey,
            nonNullable.IsEnum ? Enum.GetNames(nonNullable) : null,
            Attr<SensitiveAttribute>(attributeSources)?.Permission);
    }

    private static T? Attr<T>(ICustomAttributeProvider?[] sources) where T : Attribute =>
        sources.Where(s => s is not null)
            .SelectMany(s => s!.GetCustomAttributes(typeof(T), inherit: true).OfType<T>())
            .FirstOrDefault();

    /// <summary>Positional records use the ctor; init-property records (EF projection DTOs) use properties.</summary>
    public static IReadOnlyList<FieldModel> FromRecord(Type recordType)
    {
        var nullability = new NullabilityInfoContext();
        var ctor = recordType.GetConstructors().MaxBy(c => c.GetParameters().Length);
        if (ctor is { } c && c.GetParameters().Length > 0)
            return c.GetParameters().Select(p => From(p, nullability)).ToList();
        return recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite || p.GetSetMethod(true) is not null)
            .Select(p => From(p, nullability)).ToList();
    }
}

public sealed record OperationDefinition(
    string Id,
    string Permission,
    Type DeclaringType,
    Type InputType,
    Type? OutputType,
    MethodInfo Execute,
    Type? ExtensibleEntity,
    IReadOnlyList<FieldModel> InputFields)
{
    public string TitleKey => $"operations.{Id}.title";

    /// <summary>Owning plugin id, or null for host-compiled operations (docs/22).</summary>
    public string? Plugin { get; init; }

    public static OperationDefinition From(Type type)
    {
        var op = type.GetCustomAttribute<OperationAttribute>()
            ?? throw new InvalidOperationException($"{type.Name} lacks [Operation].");
        var permission = type.GetCustomAttribute<AuthorizeAttribute>()?.Permission
            ?? throw new InvalidOperationException(
                $"AUTH001: operation '{op.Id}' has no [Authorize] permission.");
        var input = type.GetNestedType("Input")
            ?? throw new InvalidOperationException($"{type.Name} has no Input record.");
        var output = type.GetNestedType("Output");
        var execute = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{type.Name} has no static Execute method.");

        return new OperationDefinition(
            op.Id, permission, type, input, output, execute,
            type.GetCustomAttribute<AcceptsExtensionsAttribute>()?.Entity,
            FieldModel.FromRecord(input));
    }
}

public sealed record ViewCapability(
    IReadOnlyList<string> Sortable,
    IReadOnlyList<string> Filterable,
    string? DefaultSort,
    bool DefaultSortDescending);

public sealed record ViewDefinition(
    string Id,
    string Permission,
    Type DeclaringType,
    Type QueryType,
    Type ResultType,
    MethodInfo Execute,
    IReadOnlyList<FieldModel> QueryFields,
    IReadOnlyList<FieldModel> ResultFields,
    ViewCapability Capabilities,
    Type? ExtensibleEntity)
{
    /// <summary>Owning plugin id, or null for host-compiled views (docs/22).</summary>
    public string? Plugin { get; init; }

    public static ViewDefinition From(Type type)
    {
        var view = type.GetCustomAttribute<ViewAttribute>()
            ?? throw new InvalidOperationException($"{type.Name} lacks [View].");
        var permission = type.GetCustomAttribute<AuthorizeAttribute>()?.Permission
            ?? throw new InvalidOperationException($"AUTH001: view '{view.Id}' has no [Authorize] permission.");
        var query = type.GetNestedType("Query")
            ?? throw new InvalidOperationException($"{type.Name} has no Query record.");
        var result = type.GetNestedType("Result")
            ?? throw new InvalidOperationException($"{type.Name} has no Result record.");
        var execute = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{type.Name} has no static Execute method.");

        var capabilities = new ViewCapability([], [], null, false);
        if (type.GetMethod("Capabilities", BindingFlags.Public | BindingFlags.Static) is { } capMethod)
        {
            var builder = new ViewCapabilitiesBuilder();
            capMethod.Invoke(null, [builder]);
            capabilities = builder.Build();
        }

        return new ViewDefinition(
            view.Id, permission, type, query, result, execute,
            FieldModel.FromRecord(query), FieldModel.FromRecord(result), capabilities,
            type.GetCustomAttribute<AcceptsExtensionsAttribute>()?.Entity);
    }
}

public sealed class ViewCapabilitiesBuilder
{
    private readonly List<string> sortable = [];
    private readonly List<string> filterable = [];
    private string? defaultSort;
    private bool defaultSortDescending;

    public ViewCapabilitiesBuilder Sortable(params string[] members)
    {
        sortable.AddRange(members.Select(Naming.Camel));
        return this;
    }

    public ViewCapabilitiesBuilder Filterable(params string[] members)
    {
        filterable.AddRange(members.Select(Naming.Camel));
        return this;
    }

    public ViewCapabilitiesBuilder DefaultSort(string member, bool descending = false)
    {
        defaultSort = Naming.Camel(member);
        defaultSortDescending = descending;
        return this;
    }

    public ViewCapability Build() => new(sortable, filterable, defaultSort, defaultSortDescending);
}

public sealed record DerivationDefinition(
    string Id,
    Type InputType,
    IReadOnlyList<string> DependsOn,
    MethodInfo Method)
{
    public static IEnumerable<DerivationDefinition> FromType(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.GetCustomAttribute<ServerDerivationAttribute>() is not { } attr) continue;
            var input = method.GetParameters().FirstOrDefault()?.ParameterType
                ?? throw new InvalidOperationException($"Derivation {attr.Id} needs an input parameter.");
            var depends = method.GetCustomAttribute<DependsOnAttribute>()?.Members
                .Select(Naming.Camel).ToArray() ?? [];
            yield return new DerivationDefinition(attr.Id, input, depends, method);
        }
    }
}
