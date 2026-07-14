using System.Linq.Expressions;
using System.Reflection;

namespace Tam.AspNetCore;

/// <summary>
/// The only place dynamic LINQ expressions are built. Deliberately no expression-DSL library:
/// user input becomes Expression.Constant *values*, never expression *structure* — the same
/// safety property as parameterized SQL. If the filter language grows, extend the portable Px
/// AST and compile it here, rather than adopting a string-parsed DSL.
/// </summary>
internal static class TamExpressions
{
    /// <summary>x => x.Property == value (typed equality over the authored projection).</summary>
    public static IQueryable<T> WhereEqual<T>(IQueryable<T> source, PropertyInfo property, object? value)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var body = Expression.Equal(
            Expression.Property(parameter, property),
            Expression.Constant(value, property.PropertyType));
        return source.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    /// <summary>
    /// x => ((string)(object)x.Extensions).Contains("\"key\":\"value\"") — the ExtensionData
    /// column converts to its canonical JSON string, so quoted containment is an exact match for
    /// string-typed custom fields on every relational provider. Promoted expression indexes are
    /// the performance path (docs/15); numeric extension filtering awaits real JSON translation.
    /// </summary>
    public static IQueryable<T> WhereExtensionEquals<T>(
        IQueryable<T> source, PropertyInfo extensionsProperty, string key, string value)
    {
        // SQLite stores our compact serialization ("key":"v"); PG jsonb's canonical text form
        // inserts a space ("key": "v") — match either, so the predicate is provider-agnostic.
        var jsonKey = System.Text.Json.JsonSerializer.Serialize(key);
        var jsonValue = System.Text.Json.JsonSerializer.Serialize(value);
        var parameter = Expression.Parameter(typeof(T), "x");
        var asString = Expression.Convert(
            Expression.Convert(Expression.Property(parameter, extensionsProperty), typeof(object)),
            typeof(string));
        var containsMethod = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        var body = Expression.OrElse(
            Expression.Call(asString, containsMethod, Expression.Constant($"{jsonKey}:{jsonValue}")),
            Expression.Call(asString, containsMethod, Expression.Constant($"{jsonKey}: {jsonValue}")));
        return source.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    /// <summary>OrderBy/OrderByDescending with a strongly typed key — an (object) cast would
    /// defeat EF's projection member matching and fail translation (VIEW001 territory).</summary>
    public static IQueryable<T> OrderByProperty<T>(IQueryable<T> source, PropertyInfo property, bool descending)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var lambda = Expression.Lambda(Expression.Property(parameter, property), parameter);
        var method = typeof(Queryable).GetMethods()
            .First(m => m.Name == (descending ? "OrderByDescending" : "OrderBy")
                && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(T), property.PropertyType);
        return (IQueryable<T>)method.Invoke(null, [source, lambda])!;
    }
}
