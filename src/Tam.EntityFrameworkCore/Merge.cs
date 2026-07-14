using System.Reflection;

namespace Tam.EntityFrameworkCore;

public sealed record MergeResult(
    IReadOnlyList<string> AppliedFields,
    IReadOnlyList<FieldConflict> Conflicts)
{
    public bool HasConflicts => Conflicts.Count > 0;

    public Result<T> ToConflictResult<T>() => new()
    {
        Findings = Conflicts
            .Select(c => ConcurrencyFindings.FieldConflict.At(c.Field))
            .ToList(),
    };
}

/// <summary>
/// Field-level three-way merge (docs/07): base = client-reported original, current = persisted,
/// submitted = new value. Semantic equality per value type. Non-overlapping concurrent edits
/// merge automatically; a genuine conflict returns structured data, never an exception page.
/// </summary>
public static class TamMerge
{
    public static MergeResult Apply<TEntity>(TEntity entity, object input)
        where TEntity : notnull
    {
        var applied = new List<string>();
        var conflicts = new List<FieldConflict>();
        var entityType = entity.GetType();

        foreach (var property in input.GetType().GetProperties())
        {
            if (!property.PropertyType.IsGenericType ||
                property.PropertyType.GetGenericTypeDefinition() != typeof(Change<>))
            {
                continue;
            }

            var change = property.GetValue(input);
            if (change is null) continue;   // absent = untouched

            var changeType = property.PropertyType;
            var original = changeType.GetProperty("Original")!.GetValue(change);
            var submitted = changeType.GetProperty("Value")!.GetValue(change);

            var target = entityType.GetProperty(property.Name,
                    BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"EDIT003: {entityType.Name} has no property '{property.Name}' for change-set member.");

            var current = target.GetValue(entity);
            var semantic = SemanticTypes.For(
                Nullable.GetUnderlyingType(target.PropertyType) ?? target.PropertyType);

            if (SemanticallyEqual(semantic, current, submitted))
            {
                continue;   // already resolved
            }

            if (SemanticallyEqual(semantic, current, original))
            {
                SetProperty(entity, target, submitted);
                applied.Add(Naming.Camel(property.Name));
                continue;
            }

            conflicts.Add(new FieldConflict(
                Naming.Camel(property.Name),
                ValueWrapper.Unwrap(original),
                ValueWrapper.Unwrap(current),
                ValueWrapper.Unwrap(submitted)));
        }

        return new MergeResult(applied, conflicts);
    }

    internal static bool SemanticallyEqual(SemanticType semantic, object? a, object? b)
    {
        var ua = semantic.Normalize(ValueWrapper.Unwrap(a));
        var ub = semantic.Normalize(ValueWrapper.Unwrap(b));
        if (ua is null && ub is null) return true;
        if (ua is null || ub is null) return false;
        return semantic.SemanticEquals(ua, ub);
    }

    private static void SetProperty(object entity, PropertyInfo property, object? value)
    {
        var setter = property.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException(
                $"EDIT004: {entity.GetType().Name}.{property.Name} has no setter for change application.");
        setter.Invoke(entity, [value]);
    }
}
