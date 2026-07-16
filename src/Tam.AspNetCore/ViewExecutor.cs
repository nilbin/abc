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
        // The SubtreeRead widening inside is EXECUTION-LOCAL (review-round-4 F2): the ambient
        // read set is restored on the way out. Without this, a widened view read mid-request
        // (a gate or operation going through IHostViewReader) would leave every LATER read in
        // the same scope widened — including a write handler's load of the row it edits, which
        // must see the strict filter (docs/26: "writes never widen").
        var scope = services.GetService(typeof(TenantScope)) as TenantScope;
        var priorReadSet = scope?.ReadSet ?? [];
        try
        {
            return await ExecuteWidenedAsync(viewId, query, context, ct);
        }
        finally
        {
            scope?.WidenRead(priorReadSet);
        }
    }

    private async Task<(ViewResponse? Response, Finding? Error)> ExecuteWidenedAsync(
        string viewId,
        IReadOnlyDictionary<string, string?> query,
        OperationContext context,
        CancellationToken ct)
    {
        if (!model.Views.TryGetValue(viewId, out var view))
            return (null, PipelineFindings.UnknownView.With(("view", viewId)));

        // Inactive plugin → the view does not exist for this tenant (docs/22).
        if (view.Plugin is { } plugin
            && services.GetService(typeof(ITamDb)) is ITamDb tam)
        {
            var active = await ActivationCache.ForAsync(services, tam.Db, context.TenantId.Value, ct);
            if (!active.Contains(plugin))
                return (null, PipelineFindings.UnknownView.With(("view", viewId)));
        }

        if (!context.Actor.Can(view.Permission))
            return (null, PipelineFindings.NotAuthorized.With(("permission", view.Permission)));

        // SubtreeRead (docs/26 D-H1): widen the ambient READ scope to the acting node's
        // validated subtree for this view execution. The set derives server-side from the
        // tenants table, never from client input, and every ITenantScoped source in the view's
        // query widens coherently — no per-source IgnoreQueryFilters composition to get wrong.
        // Writes never see this: the operation pipeline and stamp interceptor use Current alone.
        if (view.Capabilities.SubtreeTenantField is not null
            && services.GetService(typeof(TenantScope)) is TenantScope scope
            && services.GetService(typeof(ITamDb)) is ITamDb subtreeDb)
        {
            var activePath = await subtreeDb.Db.Set<TenantEntity>()
                .Where(t => t.Id == context.TenantId.Value)
                .Select(t => t.Path).FirstOrDefaultAsync(ct) ?? context.TenantId.Value;
            var prefix = activePath + ".";
            scope.WidenRead(await subtreeDb.Db.Set<TenantEntity>()
                .Where(t => t.Path == activePath || t.Path.StartsWith(prefix))
                .Select(t => t.Id).ToListAsync(ct));
        }

        object queryRecord;
        List<FieldFilter> fieldFilters;
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
        // runtime-defined fields can never appear in a compiled Query record (docs/15). The
        // operator set derives from the declared spec's wire kind, exactly like compiled
        // fields: equality everywhere, from/to for numbers/strings/dates, contains for strings.
        var extensionFilters = new List<ExtensionFilter>();
        if (view.ExtensibleEntity is { } extensibleEntity)
        {
            var raw = query
                .Where(kv => kv.Key.StartsWith("ext.", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(kv.Value))
                .Select(kv => (Param: kv.Key["ext.".Length..], kv.Value))
                .ToList();
            if (raw.Count > 0
                && services.GetService(typeof(IExtensionRegistry)) is IExtensionRegistry registry)
            {
                var specs = (await registry.For(
                        context.TenantId, TamModel.EntityKey(extensibleEntity), ct))
                    .Where(s => s.State is ExtensionFieldState.Active or ExtensionFieldState.Deprecated)
                    .ToDictionary(s => s.Key, StringComparer.Ordinal);
                foreach (var (param, value) in raw)
                {
                    var (key, op) = param switch
                    {
                        _ when param.EndsWith(".from", StringComparison.Ordinal) =>
                            (param[..^".from".Length], FilterOperator.From),
                        _ when param.EndsWith(".to", StringComparison.Ordinal) =>
                            (param[..^".to".Length], FilterOperator.To),
                        _ when param.EndsWith(".contains", StringComparison.Ordinal) =>
                            (param[..^".contains".Length], FilterOperator.Contains),
                        _ => (param, FilterOperator.Equal),
                    };
                    if (!specs.TryGetValue(key, out var spec)) continue;   // undeclared → ignored
                    var kind = spec.Semantic.WireKind;
                    var legal = op switch
                    {
                        FilterOperator.Contains => kind == "string",
                        FilterOperator.From or FilterOperator.To =>
                            kind is "string" or "number" or "integer" or "date",
                        _ => true,
                    };
                    if (!legal) continue;
                    if (kind is "number" or "integer"
                        && !decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture, out _))
                        return (null, PipelineFindings.InvalidInput.Create());
                    if (kind == "boolean" && value is not ("true" or "false"))
                        return (null, PipelineFindings.InvalidInput.Create());
                    extensionFilters.Add(new ExtensionFilter(key, kind, op, value!));
                }
            }
        }
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

        // Extension fields sort mechanically like they filter (docs/04): "sort=ext.{key}",
        // typed by the declared spec via the same JSON-extraction functions.
        ExtensionFilter? extensionSort = null;
        if (sort is { } s && s.StartsWith("ext.", StringComparison.Ordinal))
        {
            var key = s["ext.".Length..];
            if (view.ExtensibleEntity is { } sortEntity
                && services.GetService(typeof(IExtensionRegistry)) is IExtensionRegistry sortRegistry
                && (await sortRegistry.For(context.TenantId, TamModel.EntityKey(sortEntity), ct))
                    .FirstOrDefault(x => x.Key == key
                        && x.State is ExtensionFieldState.Active or ExtensionFieldState.Deprecated) is { } spec)
            {
                extensionSort = new ExtensionFilter(key, spec.Semantic.WireKind, FilterOperator.Equal, "");
                sort = null;
            }
            else
            {
                sort = view.Capabilities.DefaultSort;
            }
        }
        else if (sort is not null && !view.Capabilities.Sortable.Contains(sort))
        {
            sort = view.Capabilities.DefaultSort;
        }

        var page = int.TryParse(query.GetValueOrDefault("page"), out var p1) ? Math.Max(1, p1) : 1;
        var pageSize = int.TryParse(query.GetValueOrDefault("pageSize"), out var p2)
            ? Math.Clamp(p2, 1, 200) : 25;

        var isNpgsql = (services.GetService(typeof(ITamDb)) as ITamDb)?
            .Db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        var run = RunMethod.MakeGenericMethod(view.ResultType);
        var task = (Task<ViewResponse>)run.Invoke(
            null, [queryable, sort, descending, page, pageSize, fieldFilters, extensionFilters, extensionSort, isNpgsql, ct])!;
        var response = await task;

        // Read masking (docs/27 D-A3): every sensitive field the actor may not see is REMOVED from
        // the rows — for that actor the column does not exist, matching the masked manifest. Costs
        // a node conversion only when the view actually has masked fields for this actor.
        var masked = view.ResultFields
            .Where(f => f.IsMaskedFor(context.Actor))
            .Select(f => f.WireName)
            .ToList();
        if (masked.Count > 0)
        {
            var rows = response.Rows.Select(row =>
            {
                var node = System.Text.Json.JsonSerializer.SerializeToNode(row, TamJson.Options)!.AsObject();
                foreach (var name in masked) node.Remove(name);
                return (object)node;
            }).ToList();
            response = response with { Rows = rows };
        }
        return (response, null);
    }

    private static readonly MethodInfo RunMethod =
        typeof(ViewExecutor).GetMethod(nameof(Run), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal enum FilterOperator { Equal, From, To, Contains }

    internal sealed record FieldFilter(PropertyInfo Property, FilterOperator Op, object? Value);

    internal sealed record ExtensionFilter(string Key, string WireKind, FilterOperator Op, string Value);

    /// <summary>
    /// Declared filterable fields → typed predicates. One declaration yields the operators the
    /// field's type supports mechanically: `field=` equality for everything, `field.from=` /
    /// `field.to=` inclusive ranges for dates, numbers and ordinal strings, `field.contains=`
    /// substring for strings. Values are parsed into constants — never expression structure.
    /// </summary>
    private static List<FieldFilter> BindFilters(
        ViewDefinition view, IReadOnlyDictionary<string, string?> query)
    {
        var filters = new List<FieldFilter>();
        foreach (var field in view.Capabilities.Filterable)
        {
            var property = view.ResultType.GetProperties()
                .FirstOrDefault(p => Naming.Camel(p.Name) == field);
            if (property is null) continue;
            var comparable = ComparableType(property.PropertyType);

            if (query.GetValueOrDefault(field) is { Length: > 0 } equal)
                filters.Add(new(property, FilterOperator.Equal, ParseValue(property.PropertyType, equal)));
            if (comparable == typeof(string)
                && query.GetValueOrDefault($"{field}.contains") is { Length: > 0 } substring)
                filters.Add(new(property, FilterOperator.Contains, substring));
            if (Rangeable.Contains(comparable))
            {
                if (query.GetValueOrDefault($"{field}.from") is { Length: > 0 } from)
                    filters.Add(new(property, FilterOperator.From, ParseValue(comparable, from)));
                if (query.GetValueOrDefault($"{field}.to") is { Length: > 0 } to)
                    filters.Add(new(property, FilterOperator.To, ParseValue(comparable, to)));
            }
        }
        return filters;
    }

    /// <summary>The type range/contains operators reason about: Nullable and semantic wrappers unwrapped.</summary>
    private static Type ComparableType(Type propertyType)
    {
        var nonNullable = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        return ValueWrapper.UnderlyingType(nonNullable) ?? nonNullable;
    }

    private static readonly HashSet<Type> Rangeable =
        [typeof(string), typeof(int), typeof(long), typeof(decimal), typeof(DateOnly), typeof(DateTimeOffset)];

    private static async Task<ViewResponse> Run<T>(
        object queryable, string? sort, bool descending, int page, int pageSize,
        List<FieldFilter> fieldFilters,
        List<ExtensionFilter> extensionFilters,
        ExtensionFilter? extensionSort,
        bool isNpgsql,
        CancellationToken ct)
    {
        var source = (IQueryable<T>)queryable;

        // Declared filters compose over the authored projection — the capability IS the filter
        // (docs/04): no per-view Where code, and EF pushes it into SQL through the projection.
        foreach (var filter in fieldFilters)
            source = filter.Op switch
            {
                FilterOperator.Contains =>
                    TamExpressions.WhereContains(source, filter.Property, (string)filter.Value!),
                FilterOperator.From =>
                    TamExpressions.WhereCompare(source, filter.Property, filter.Value, greaterOrEqual: true),
                FilterOperator.To =>
                    TamExpressions.WhereCompare(source, filter.Property, filter.Value, greaterOrEqual: false),
                _ => TamExpressions.WhereEqual(source, filter.Property, filter.Value),
            };

        // Extension filters need SQL translation of the converted JSON column, so they apply
        // only on EF-backed queries (in-memory sources would crash on the converted cast).
        if (extensionFilters.Count > 0
            && typeof(T).GetProperty("Extensions") is { } extensionsProperty
            && source.Provider is Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider)
        {
            foreach (var filter in extensionFilters)
                source = TamExpressions.WhereExtension(
                    source, extensionsProperty, filter.Key, filter.WireKind, filter.Op, filter.Value, isNpgsql);
        }

        if (extensionSort is not null
            && typeof(T).GetProperty("Extensions") is { } sortProperty
            && source.Provider is Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider)
        {
            source = TamExpressions.OrderByExtension(
                source, sortProperty, extensionSort.Key, extensionSort.WireKind, descending);
        }
        else if (sort is not null)
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
        if (target == typeof(DateOnly))
            return DateOnly.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (target == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
        throw new NotSupportedException($"Query binding for {target.Name} is not supported.");
    }
}
