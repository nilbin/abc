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
    IReadOnlyList<string>? EnumOptions)
{
    public static FieldModel From(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        var clr = parameter.ParameterType;
        var isChange = clr.IsGenericType && clr.GetGenericTypeDefinition() == typeof(Change<>);
        var effective = isChange ? clr.GetGenericArguments()[0] : clr;
        var nonNullable = Nullable.GetUnderlyingType(effective) ?? effective;

        var required = !isChange
            && !parameter.HasDefaultValue
            && Nullable.GetUnderlyingType(effective) is null
            && (effective.IsValueType || nullability.Create(parameter).WriteState == NullabilityState.NotNull);

        var labelKey = parameter.GetCustomAttribute<LabelKeyAttribute>()?.Key
            ?? nonNullable.GetCustomAttribute<LabelKeyAttribute>()?.Key
            ?? $"labels.{Naming.Kebab(parameter.Name!)}";

        return new FieldModel(
            parameter.Name!,
            Naming.Camel(parameter.Name!),
            clr,
            effective,
            isChange,
            required,
            SemanticTypes.For(nonNullable),
            labelKey,
            nonNullable.IsEnum ? Enum.GetNames(nonNullable) : null);
    }

    public static IReadOnlyList<FieldModel> FromRecord(Type recordType)
    {
        var ctor = recordType.GetConstructors().MaxBy(c => c.GetParameters().Length)
            ?? throw new InvalidOperationException($"{recordType.Name} has no public constructor.");
        var nullability = new NullabilityInfoContext();
        return ctor.GetParameters().Select(p => From(p, nullability)).ToList();
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
