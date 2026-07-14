using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// Executes views: binds the Query record from the query string, injects services, applies
/// declared sort capabilities and paging. Filtering is the Query record itself — the view's
/// own LINQ is the filter implementation (docs/04).
/// </summary>
public sealed class ViewExecutor(TamModel model, IServiceProvider services)
{
    public async Task<(ViewResponse? Response, Finding? Error)> ExecuteAsync(
        string viewId,
        IReadOnlyDictionary<string, string?> query,
        OperationContext context,
        CancellationToken ct)
    {
        if (!model.Views.TryGetValue(viewId, out var view))
            return (null, PipelineFindings.UnknownView.With(("view", viewId)));
        if (!context.Actor.Can(view.Permission))
            return (null, PipelineFindings.NotAuthorized.With(("permission", view.Permission)));

        object queryRecord;
        List<(PropertyInfo Property, object? Value)> fieldFilters;
        try
        {
            queryRecord = BindQuery(view, query);
            fieldFilters = BindFilters(view, query);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or NotSupportedException or OverflowException)
        {
            return (null, PipelineFindings.InvalidInput.Create());
        }

        // Tenant extension fields filter by "ext.{key}" params — necessarily mechanical:
        // runtime-defined fields can never appear in a compiled Query record (docs/15).
        var extensionFilters = view.ExtensibleEntity is null
            ? []
            : query.Where(kv => kv.Key.StartsWith("ext.", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(kv.Value))
                .Select(kv => (Key: kv.Key["ext.".Length..], Value: kv.Value!))
                .ToList();
        var args = view.Execute.GetParameters().Select(p =>
        {
            if (p.Position == 0) return queryRecord;
            if (p.ParameterType == typeof(OperationContext)) return context;
            if (p.ParameterType == typeof(CancellationToken)) return (object?)ct;
            return services.GetService(p.ParameterType)
                ?? throw new InvalidOperationException(
                    $"DI001: cannot bind view parameter '{p.Name}' ({p.ParameterType.Name}).");
        }).ToArray();

        var queryable = view.Execute.Invoke(null, args)!;

        var sort = query.GetValueOrDefault("sort") ?? view.Capabilities.DefaultSort;
        var descending = query.GetValueOrDefault("dir") is "desc"
            || (query.GetValueOrDefault("sort") is null && view.Capabilities.DefaultSortDescending);
        if (sort is not null && !view.Capabilities.Sortable.Contains(sort))
            sort = view.Capabilities.DefaultSort;

        var page = int.TryParse(query.GetValueOrDefault("page"), out var p1) ? Math.Max(1, p1) : 1;
        var pageSize = int.TryParse(query.GetValueOrDefault("pageSize"), out var p2)
            ? Math.Clamp(p2, 1, 200) : 25;

        var run = RunMethod.MakeGenericMethod(view.ResultType);
        var task = (Task<ViewResponse>)run.Invoke(
            null, [queryable, sort, descending, page, pageSize, fieldFilters, extensionFilters, ct])!;
        return (await task, null);
    }

    private static readonly MethodInfo RunMethod =
        typeof(ViewExecutor).GetMethod(nameof(Run), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Declared filterable fields present in the query string → typed equality values.</summary>
    private static List<(PropertyInfo Property, object? Value)> BindFilters(
        ViewDefinition view, IReadOnlyDictionary<string, string?> query)
    {
        var filters = new List<(PropertyInfo, object?)>();
        foreach (var field in view.Capabilities.Filterable)
        {
            var raw = query.GetValueOrDefault(field);
            if (string.IsNullOrEmpty(raw)) continue;
            var property = view.ResultType.GetProperties()
                .FirstOrDefault(p => Naming.Camel(p.Name) == field);
            if (property is null) continue;
            filters.Add((property, ParseValue(property.PropertyType, raw)));
        }
        return filters;
    }

    private static async Task<ViewResponse> Run<T>(
        object queryable, string? sort, bool descending, int page, int pageSize,
        List<(PropertyInfo Property, object? Value)> fieldFilters,
        List<(string Key, string Value)> extensionFilters,
        CancellationToken ct)
    {
        var source = (IQueryable<T>)queryable;

        // Declared filters compose over the authored projection — the capability IS the filter
        // (docs/04): no per-view Where code, and EF pushes it into SQL through the projection.
        foreach (var (property, value) in fieldFilters)
            source = TamExpressions.WhereEqual(source, property, value);

        // Extension filters need SQL translation of the converted JSON column, so they apply
        // only on EF-backed queries (in-memory sources would crash on the converted cast).
        if (extensionFilters.Count > 0
            && typeof(T).GetProperty("Extensions") is { } extensionsProperty
            && source.Provider is Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider)
        {
            foreach (var (key, value) in extensionFilters)
                source = TamExpressions.WhereExtensionEquals(source, extensionsProperty, key, value);
        }

        if (sort is not null)
        {
            var member = typeof(T).GetProperties().FirstOrDefault(
                p => Naming.Camel(p.Name) == sort);
            if (member is not null)
                source = TamExpressions.OrderByProperty(source, member, descending);
        }

        int total;
        List<T> rows;
        if (source.Provider is Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider)
        {
            total = await source.CountAsync(ct);
            rows = await source.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        }
        else
        {
            total = source.Count();
            rows = source.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }

        return new ViewResponse(rows.Cast<object>().ToList(), total, page, pageSize);
    }

    /// <summary>Binds the Query record's ctor from query-string values; missing members become defaults.</summary>
    internal static object BindQuery(ViewDefinition view, IReadOnlyDictionary<string, string?> query)
    {
        var ctor = view.QueryType.GetConstructors().MaxBy(c => c.GetParameters().Length)!;
        var args = ctor.GetParameters().Select(parameter =>
        {
            var raw = query.GetValueOrDefault(Naming.Camel(parameter.Name!));
            if (string.IsNullOrEmpty(raw))
                return parameter.HasDefaultValue ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null;
            return ParseValue(parameter.ParameterType, raw);
        }).ToArray();
        return ctor.Invoke(args);
    }

    internal static object? ParseValue(Type type, string raw)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (ValueWrapper.IsWrapper(target))
            return ValueWrapper.Wrap(target, ParseValue(ValueWrapper.UnderlyingType(target)!, raw));
        if (target.IsEnum) return Enum.Parse(target, raw, ignoreCase: true);
        if (target == typeof(string)) return raw;
        if (target == typeof(Guid)) return Guid.Parse(raw);
        if (target == typeof(int)) return int.Parse(raw);
        if (target == typeof(long)) return long.Parse(raw);
        if (target == typeof(bool)) return bool.Parse(raw);
        if (target == typeof(decimal)) return decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (target == typeof(DateOnly)) return DateOnly.Parse(raw);
        if (target == typeof(DateTimeOffset)) return DateTimeOffset.Parse(raw);
        throw new NotSupportedException($"Query binding for {target.Name} is not supported.");
    }
}
