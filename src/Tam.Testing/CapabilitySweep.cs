using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Tam.Testing;

/// <summary>One capability execution that failed: the view, the query that broke it, and why.</summary>
public sealed record SweepFailure(string ViewId, string Query, string Error);

/// <summary>What the sweep covered and what it couldn't reach.</summary>
public sealed record SweepReport(
    int ViewsExecuted,
    int QueriesExecuted,
    IReadOnlyList<string> SkippedViews,   // plugin-inactive or unreachable for the sweep actor
    IReadOnlyList<SweepFailure> Failures)
{
    public void ThrowIfFailed()
    {
        if (Failures.Count > 0)
            throw new TamAssertionException(
                $"capability sweep: {Failures.Count} declared capabilities failed to execute:\n"
                + string.Join("\n", Failures.Select(f => $"  {f.ViewId} ?{f.Query} → {f.Error}")));
    }
}

/// <summary>
/// The "verify the whole setup" button (tutorial Step 11): executes EVERY view's declared
/// capabilities — default sort, every sortable field in both directions, every filterable field
/// with a type-appropriate probe value — through the real pipeline against the real database
/// provider. A view that compiles but cannot TRANSLATE (the TAM007 class of bug: green build,
/// 500 on the first sorted request) fails here, named, before it ships. Data is irrelevant:
/// zero rows still exercises SQL generation.
/// </summary>
public static class CapabilitySweep
{
    public static async Task<SweepReport> RunAsync<TDb>(
        TamTestHost<TDb> host, string tenantId, CancellationToken ct = default)
        where TDb : DbContext
    {
        // Authorization must never short-circuit the sweep: the actor carries every view's
        // permission AND its widening twin explicitly (reserved atoms included — this is a
        // test harness, not a production grant).
        var permissions = host.Model.Views.Values
            .SelectMany(v => new[]
            {
                v.Permission,
                v.DeclaringType.GetCustomAttribute<WidensAttribute>()?.Permission,
            })
            .OfType<string>()
            .ToArray();
        var actor = host.Actor(tenantId, permissions);

        var skipped = new List<string>();
        var failures = new List<SweepFailure>();
        var viewsExecuted = 0;
        var queriesExecuted = 0;

        foreach (var view in host.Model.Views.Values.OrderBy(v => v.Id))
        {
            var queries = Probes(view).ToList();
            var reached = false;
            foreach (var query in queries)
            {
                ct.ThrowIfCancellationRequested();
                string? failure = null;
                try
                {
                    var (response, error) = await actor.QueryAsync(view.Id, query, ct);
                    if (error is { } e)
                    {
                        // Plugin-inactive views answer unknown-view for the whole loop — skip
                        // once, not once per probe. Anything else is a real failure.
                        if (e.Code.Contains("unknown", StringComparison.Ordinal))
                        {
                            skipped.Add(view.Id);
                            break;
                        }
                        failure = e.Code;
                    }
                    else
                    {
                        reached = true;
                    }
                }
                catch (Exception ex)
                {
                    failure = $"{ex.GetType().Name}: {Root(ex).Message}";
                }
                queriesExecuted++;
                if (failure is not null)
                    failures.Add(new SweepFailure(view.Id, Describe(query), failure));
            }
            if (reached) viewsExecuted++;
        }
        return new SweepReport(viewsExecuted, queriesExecuted, skipped, failures);
    }

    /// <summary>Every query the declared capabilities promise to answer.</summary>
    private static IEnumerable<Dictionary<string, string?>> Probes(ViewDefinition view)
    {
        // The bare default-sort execution first — the page-load query.
        yield return [];

        foreach (var field in view.Capabilities.Sortable)
        {
            yield return new() { ["sort"] = field, ["pageSize"] = "1" };
            yield return new() { ["sort"] = field, ["dir"] = "desc", ["pageSize"] = "1" };
        }

        foreach (var field in view.Capabilities.Filterable)
        {
            var property = view.ResultFields.FirstOrDefault(f => f.WireName == field);
            if (property is null) continue;
            var probe = ProbeValue(property);
            if (probe is null) continue;
            yield return new() { [field] = probe, ["pageSize"] = "1" };

            var comparable = Comparable(property.EffectiveType);
            if (comparable == typeof(string))
                yield return new() { [$"{field}.contains"] = "a", ["pageSize"] = "1" };
            if (Rangeable.Contains(comparable))
            {
                yield return new() { [$"{field}.from"] = probe, ["pageSize"] = "1" };
                yield return new() { [$"{field}.to"] = probe, ["pageSize"] = "1" };
            }
        }
    }

    private static readonly HashSet<Type> Rangeable =
        [typeof(string), typeof(int), typeof(long), typeof(decimal), typeof(DateOnly), typeof(DateTimeOffset)];

    private static Type Comparable(Type type)
    {
        var nonNullable = Nullable.GetUnderlyingType(type) ?? type;
        return ValueWrapper.UnderlyingType(nonNullable) ?? nonNullable;
    }

    private static string? ProbeValue(FieldModel field)
    {
        var t = Comparable(field.EffectiveType);
        if (t == typeof(string)) return "a";
        if (t == typeof(int) || t == typeof(long)) return "1";
        if (t == typeof(decimal) || t == typeof(double)) return "1";
        if (t == typeof(bool)) return "true";
        if (t == typeof(DateOnly)) return "2026-01-01";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "2026-01-01T00:00:00Z";
        if (t == typeof(Guid)) return Guid.Empty.ToString();
        if (t.IsEnum) return Enum.GetNames(t) is { Length: > 0 } names
            ? char.ToLowerInvariant(names[0][0]) + names[0][1..] : null;
        return null;   // object-shaped fields have no mechanical probe
    }

    private static Exception Root(Exception ex) => ex.InnerException is { } inner ? Root(inner) : ex;

    private static string Describe(Dictionary<string, string?> query) =>
        query.Count == 0 ? "(default sort)" : string.Join("&", query.Select(kv => $"{kv.Key}={kv.Value}"));
}
