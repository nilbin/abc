using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public interface IActorProvider
{
    Actor GetActor(HttpContext http);
}

public interface ITenantProvider
{
    TenantId GetTenant(HttpContext http);
}

/// <summary>Development default: a single actor with every permission. Replace per application.</summary>
public sealed class DevActorProvider : IActorProvider
{
    public Actor GetActor(HttpContext http) => new("dev", "Development User", new HashSet<string> { "*" });
}

public sealed class FixedTenantProvider(string tenant) : ITenantProvider
{
    public TenantId GetTenant(HttpContext http) => new(tenant);
}

/// <summary>
/// Registry-backed actor resolution (decision D1): grants come from the roles table; only the
/// role-name source (header, JWT claim, session) and display naming are application decisions.
/// </summary>
public class RoleActorProvider(
    Func<HttpContext, string> roleName,
    Func<string, string>? displayName = null) : IActorProvider
{
    public Actor GetActor(HttpContext http)
    {
        var name = roleName(http);
        var tenant = http.RequestServices.GetRequiredService<ITenantProvider>().GetTenant(http);
        var db = http.RequestServices.GetRequiredService<ITamDb>().Db;

        var role = db.Set<RoleEntity>().FirstOrDefault(
            x => x.TenantId == tenant.Value && x.Name == name);

        return new Actor(
            name,
            displayName?.Invoke(name) ?? name,
            role?.Permissions() ?? new HashSet<string>());
    }
}

public static class TamAspNetCore
{
    public static IServiceCollection AddTam<TDbContext>(this IServiceCollection services, TamModel model)
        where TDbContext : DbContext
    {
        services.AddSingleton(model);
        services.AddScoped(sp => new OperationExecutor(model, sp, s => s.GetRequiredService<TDbContext>()));
        services.AddScoped(sp => new ViewExecutor(model, sp));
        services.AddScoped(sp => new ResolveExecutor(model, sp.GetRequiredService<OperationExecutor>(), sp));
        services.AddScoped<IExtensionRegistry>(sp => new PluginAwareExtensionRegistry(
            new EfExtensionRegistry(sp.GetRequiredService<TDbContext>()), model, sp.GetRequiredService<TDbContext>()));
        services.AddScoped<ITamDb>(sp => new TamDb(sp.GetRequiredService<TDbContext>()));
        services.AddSingleton<IActorProvider, DevActorProvider>();
        services.AddSingleton<ITenantProvider>(new FixedTenantProvider("demo"));
        services.AddSingleton<EffectBroadcaster>();
        services.AddSingleton<IOutboxTransport>(sp =>
            new BroadcasterOutboxTransport(sp.GetRequiredService<EffectBroadcaster>()));
        services.AddHostedService(sp => new OutboxDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
            sp.GetRequiredService<IOutboxTransport>(),
            model));
        return services;
    }

    public static WebApplication MapTam(this WebApplication app)
    {
        var model = app.Services.GetRequiredService<TamModel>();

        app.MapPost("/api/operations/{operationId}", async (
            string operationId, HttpContext http, OperationExecutor executor, CancellationToken ct) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, TamJson.Options, ct);
            var context = BuildContext(http, model);
            var response = await executor.ExecuteAsync(operationId, body, context, ct);
            return Results.Json(response, TamJson.Options, statusCode: StatusFor(response));
        });

        app.MapGet("/api/views/{viewId}", async (
            string viewId, HttpContext http, ViewExecutor executor, CancellationToken ct) =>
        {
            var query = http.Request.Query.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString());
            var context = BuildContext(http, model);
            var (response, error) = await executor.ExecuteAsync(viewId, query, context, ct);
            return error is not null
                ? Results.Json(new { findings = new[] { model.Locales.Resolve(error, context.Culture) } },
                    TamJson.Options, statusCode: ErrorStatus(error))
                : Results.Json(response, TamJson.Options);
        });

        app.MapPost("/api/forms/{formId}/resolve", async (
            string formId, HttpContext http, ResolveExecutor executor, CancellationToken ct) =>
        {
            var request = await JsonSerializer.DeserializeAsync<ResolveExecutor.ResolveRequest>(
                http.Request.Body, TamJson.Options, ct);
            var context = BuildContext(http, model);
            var (response, error) = await executor.ResolveAsync(formId, request!, context, ct);
            return error is not null
                ? Results.Json(new { findings = new[] { model.Locales.Resolve(error, context.Culture) } },
                    TamJson.Options, statusCode: ErrorStatus(error))
                : Results.Json(response, TamJson.Options);
        });

        app.MapGet("/api/manifest", async (
            HttpContext http, IExtensionRegistry registry, ITamDb tam, CancellationToken ct) =>
        {
            var context = BuildContext(http, model);
            var overlay = await registry.All(context.TenantId, ct);
            var activePlugins = await PluginActivations.ActiveAsync(tam.Db, context.TenantId.Value, ct);
            var manifest = ManifestBuilder.Build(
                model, overlay, revision: OverlayRevision(overlay, activePlugins), activePlugins);
            manifest = manifest with
            {
                Catalogs = MergeExtensionLabels(manifest.Catalogs, overlay, model),
                ActorPermissions = context.Actor.Permissions.ToList(),
            };
            return Results.Json(manifest, TamJson.Options);
        });

        // Plugin-shipped inbound integrations (docs/22): activation-gated, inbox-idempotent.
        app.MapPost("/api/integrations/{integrationId}", async (
            string integrationId, HttpContext http, OperationExecutor executor,
            ITamDb tam, CancellationToken ct) =>
        {
            if (!model.Integrations.TryGetValue(integrationId, out var integration))
                return Results.NotFound();

            var context = BuildContext(http, model);
            var active = await PluginActivations.ActiveAsync(tam.Db, context.TenantId.Value, ct);
            if (!active.Contains(integration.PluginId))
                return Results.NotFound();   // inactive plugin → the integration does not exist

            var payload = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, TamJson.Options, ct);
            var results = await PluginIntegrationRunner.RunAsync(integration, payload, executor, context, tam.Db, ct);
            return Results.Json(new { results }, TamJson.Options);
        });

        app.MapPost("/api/mcp", McpEndpoint.Handle);

        app.MapGet("/api/events", (HttpContext http, EffectBroadcaster broadcaster, CancellationToken ct) =>
            broadcaster.Stream(http, ct));

        app.MapGet("/openapi.json", (HttpContext http) => OpenApiEndpoint.Handle(http, model));

        return app;
    }

    public static OperationContext BuildContext(HttpContext http, TamModel model)
    {
        var actor = http.RequestServices.GetRequiredService<IActorProvider>().GetActor(http);
        var tenant = http.RequestServices.GetRequiredService<ITenantProvider>().GetTenant(http);

        var requested = http.Request.Query.TryGetValue("culture", out var q) && q.Count > 0
            ? q[0]
            : http.Request.Headers.AcceptLanguage.ToString().Split(',').FirstOrDefault()?.Split(';')[0].Trim();
        var culture = requested is { Length: > 0 } &&
            model.Locales.Cultures.Contains(requested, StringComparer.OrdinalIgnoreCase)
                ? requested
                : model.DefaultCulture;

        return new OperationContext
        {
            Actor = actor,
            TenantId = tenant,
            Source = InvocationSource.Web,
            Culture = culture,
            IdempotencyKey = http.Request.Headers["X-Idempotency-Key"].FirstOrDefault(),
            CorrelationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                ?? http.TraceIdentifier,
            Services = http.RequestServices,
        };
    }

    private static int StatusFor(OperationResponse response)
    {
        if (response.Conflicts is { Count: > 0 }) return StatusCodes.Status409Conflict;
        if (!response.Findings.Any(f => f.Severity == FindingSeverity.Error)) return StatusCodes.Status200OK;
        return response.Findings.Any(f => f.Code == PipelineFindings.NotAuthorized.Code)
            ? StatusCodes.Status403Forbidden
            : response.Findings.Any(f => f.Code == PipelineFindings.UnknownOperation.Code)
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status422UnprocessableEntity;
    }

    private static int ErrorStatus(Finding error) =>
        error.Code == PipelineFindings.NotAuthorized.Code ? StatusCodes.Status403Forbidden
        : error.Code == PipelineFindings.UnknownView.Code ? StatusCodes.Status404NotFound
        : StatusCodes.Status422UnprocessableEntity;

    /// <summary>Content-derived revision: any overlay change (add, retire, relabel) or plugin
    /// activation flip moves it, so clients know their manifest is stale.</summary>
    private static long OverlayRevision(
        IReadOnlyDictionary<string, IReadOnlyList<ExtensionFieldSpec>> overlay,
        IReadOnlySet<string>? activePlugins = null)
    {
        var fingerprint = string.Join("|", overlay
            .OrderBy(kv => kv.Key)
            .SelectMany(kv => kv.Value.Select(s => $"{kv.Key}/{s.Key}/{s.Type}/{s.State}/{s.Required}/{s.MaxLength}")))
            + "||" + string.Join(",", (activePlugins ?? new HashSet<string>()).Order());
        return BitConverter.ToInt64(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint)), 0) & long.MaxValue;
    }

    /// <summary>Tenant field labels/descriptions merge into the catalogs under "ext.{key}" (docs/15, docs/21).</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MergeExtensionLabels(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> catalogs,
        IReadOnlyDictionary<string, IReadOnlyList<ExtensionFieldSpec>> overlay,
        TamModel model)
    {
        var merged = catalogs.ToDictionary(
            kv => kv.Key,
            kv => new Dictionary<string, string>(kv.Value));

        foreach (var spec in overlay.Values.SelectMany(v => v))
        {
            foreach (var (culture, catalog) in merged)
            {
                var label = spec.Labels.GetValueOrDefault(culture)
                    ?? spec.Labels.GetValueOrDefault(model.DefaultCulture)
                    ?? spec.Labels.Values.FirstOrDefault()
                    ?? spec.Key;
                catalog[$"ext.{spec.Key}"] = label;
                var description = spec.Descriptions?.GetValueOrDefault(culture)
                    ?? spec.Descriptions?.GetValueOrDefault(model.DefaultCulture);
                if (description is not null)
                    catalog[$"ext.{spec.Key}.description"] = description;
            }
        }

        return merged.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)kv.Value);
    }
}
