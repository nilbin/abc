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
            .Select(c => ConcurrencyFindings.FieldConflict.With(("reason", c.Reason)).At(c.Field))
            .ToList(),
        Conflicts = Conflicts,
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

            // No EFFECTIVE user change (docs/40): the form always submits complete Change<T> state,
            // so an untouched field arrives with Original == Value. It contributes no patch and takes
            // NO concurrency check — a concurrent writer's change to a field the user never touched
            // must not surface as a conflict (that would block every unrelated edit). This branch is
            // FIRST so complete submission stays as quiet as the old sparse omission.
            if (SemanticallyEqual(semantic, original, submitted))
            {
                continue;
            }

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
                ValueWrapper.Unwrap(submitted),
                // A null base against a non-null current means the caller never SENT the
                // original — the raw-wire mistake — not that someone edited concurrently.
                Reason: original is null && current is not null ? "original-missing" : "stale"));
        }

        return new MergeResult(applied, conflicts);
    }

    /// <summary>Semantic equality for two values of one field's type — the ONE change-detection
    /// primitive (docs/40): TamMerge's no-op/apply/conflict decisions, the extension merge, and
    /// <c>DerivationContext.WasChanged</c> (Original != Value) all key off it, so "changed" means the
    /// same thing everywhere. Unwraps the semantic wrapper and normalizes before comparing.</summary>
    public static bool SemanticallyEqual(SemanticType semantic, object? a, object? b)
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
