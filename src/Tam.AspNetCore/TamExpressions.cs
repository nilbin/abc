using System.Linq.Expressions;
using System.Reflection;
using Tam.EntityFrameworkCore;

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
    /// Inclusive range bound: x => x.Property >= value (or &lt;=). Dates and numbers use the
    /// lifted comparison operator (a null cell is outside every range); strings — including
    /// string-backed semantic wrappers like OrderNumber — compare ordinally via string.Compare,
    /// which EF translates on every relational provider.
    /// </summary>
    public static IQueryable<T> WhereCompare<T>(
        IQueryable<T> source, PropertyInfo property, object? value, bool greaterOrEqual)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        Expression member = Expression.Property(parameter, property);
        var nonNullable = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        // Semantic wrappers compare as their store type: the (underlying)(object) double-cast is
        // erased by EF against the value-converted column (same pattern as extension containment).
        if (ValueWrapper.UnderlyingType(nonNullable) is { } underlying)
        {
            member = Expression.Convert(Expression.Convert(member, typeof(object)), underlying);
            nonNullable = underlying;
        }

        Expression body;
        if (nonNullable == typeof(string))
        {
            var compare = typeof(string).GetMethod(
                nameof(string.Compare), [typeof(string), typeof(string)])!;
            var comparison = Expression.Call(compare, member, Expression.Constant(value, typeof(string)));
            body = greaterOrEqual
                ? Expression.GreaterThanOrEqual(comparison, Expression.Constant(0))
                : Expression.LessThanOrEqual(comparison, Expression.Constant(0));
        }
        else
        {
            var constant = Expression.Constant(value, member.Type);
            body = greaterOrEqual
                ? Expression.GreaterThanOrEqual(member, constant)
                : Expression.LessThanOrEqual(member, constant);
        }
        return source.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    /// <summary>x => x.Property.Contains(value) for string fields and string-backed wrappers.</summary>
    public static IQueryable<T> WhereContains<T>(IQueryable<T> source, PropertyInfo property, string value)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        Expression member = Expression.Property(parameter, property);
        var nonNullable = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (ValueWrapper.UnderlyingType(nonNullable) == typeof(string))
            member = Expression.Convert(Expression.Convert(member, typeof(object)), typeof(string));
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        var body = Expression.Call(member, contains, Expression.Constant(value));
        return source.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    /// <summary>
    /// Typed predicate over a tenant/plugin/package extension field: the ExtensionData column
    /// (via the converted-column double-cast) feeds <see cref="Tam.EntityFrameworkCore.TamDbFunctions"/>,
    /// whose per-provider translations do real JSON extraction — exact equality, substring, and
    /// inclusive ranges for numbers, strings and ISO dates (ordinal-safe). The key comes from
    /// the declared overlay; user input only ever becomes the comparison VALUE constant.
    /// Promoted expression indexes remain the performance path (docs/15).
    /// </summary>
    public static IQueryable<T> WhereExtension<T>(
        IQueryable<T> source, PropertyInfo extensionsProperty, string key, string wireKind,
        ViewExecutor.FilterOperator op, string raw)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var asString = Expression.Convert(
            Expression.Convert(Expression.Property(parameter, extensionsProperty), typeof(object)),
            typeof(string));
        var keyConstant = Expression.Constant(key);

        Expression body;
        if (wireKind is "number" or "integer")
        {
            var member = Expression.Call(
                typeof(TamDbFunctions).GetMethod(nameof(TamDbFunctions.JsonNumber))!,
                asString, keyConstant);
            var value = Expression.Constant(
                (double?)double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
                typeof(double?));
            body = op switch
            {
                ViewExecutor.FilterOperator.From => Expression.GreaterThanOrEqual(member, value),
                ViewExecutor.FilterOperator.To => Expression.LessThanOrEqual(member, value),
                _ => Expression.Equal(member, value),
            };
        }
        else
        {
            var member = Expression.Call(
                typeof(TamDbFunctions).GetMethod(nameof(TamDbFunctions.JsonValue))!,
                asString, keyConstant);
            var value = Expression.Constant(raw);
            var compare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
            body = op switch
            {
                ViewExecutor.FilterOperator.Contains => Expression.Call(
                    member, typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!, value),
                ViewExecutor.FilterOperator.From => Expression.GreaterThanOrEqual(
                    Expression.Call(compare, member, value), Expression.Constant(0)),
                ViewExecutor.FilterOperator.To => Expression.LessThanOrEqual(
                    Expression.Call(compare, member, value), Expression.Constant(0)),
                _ => Expression.Equal(member, value),
            };
        }
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
