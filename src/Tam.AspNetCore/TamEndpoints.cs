using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static partial class TamAspNetCore
{
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
            var activePlugins = await ActivationCache.ForAsync(http.RequestServices, tam.Db, context.TenantId.Value, ct);
            var revision = OverlayRevision(overlay, activePlugins);

            // The manifest is a pure function of (model, overlay, activePlugins, actor permissions).
            // The overlay+activation queries are cheap; the reflection rebuild + serialization are
            // not — so serve a content ETag and answer 304 when the client already has this version,
            // skipping the build entirely (review-round-3 #5).
            var etag = ManifestETag(revision, context.Actor.Permissions);
            if (http.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                http.Response.Headers["ETag"] = etag;
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            var manifest = ManifestBuilder.Build(model, overlay, revision: revision, activePlugins);
            // Read masking (docs/27 D-A3): sensitive fields the actor may not see are absent from the
            // manifest entirely. Deterministic in the actor's permission set, so the ETag holds.
            manifest = ManifestBuilder.MaskSensitive(manifest, context.Actor) with
            {
                Catalogs = MergeExtensionLabels(manifest.Catalogs, overlay, model),
                ActorPermissions = context.Actor.Permissions.ToList(),
            };
            http.Response.Headers["ETag"] = etag;
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
            var active = await ActivationCache.ForAsync(http.RequestServices, tam.Db, context.TenantId.Value, ct);
            if (!active.Contains(integration.PluginId))
                return Results.NotFound();   // inactive plugin → the integration does not exist

            JsonElement payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, TamJson.Options, ct);
            }
            catch (JsonException)
            {
                // A partner posting malformed JSON is a client error, not a server fault.
                return Results.Json(
                    new { findings = new[] { new { code = "integrations.malformed-payload" } } },
                    TamJson.Options, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            var results = await PluginIntegrationRunner.RunAsync(integration, payload, executor, context, tam.Db, ct);
            return Results.Json(new { results }, TamJson.Options);
        });

        app.MapPost("/api/mcp", McpEndpoint.Handle);

        app.MapGet("/api/events", (HttpContext http, EffectBroadcaster broadcaster, CancellationToken ct) =>
            broadcaster.Stream(http, ct));

        // The account's standable nodes (docs/26 D-H3/D-H4): memberships + cascaded descendants,
        // labeled by path. Backs the SPA's company/create-target pickers; the login picker uses the
        // same TenantTree set server-side. Identity-gated only — the list is the account's own reach.
        app.MapGet("/api/tenants/standable", (HttpContext http, ITamDb tam) =>
        {
            var accountClaim = http.User.FindFirst(ClaimsActorProvider.AccountClaim)?.Value;
            if (!Guid.TryParse(accountClaim, out var accountId)) return Results.Unauthorized();
            var active = TamTenant.Resolve(http).Value;
            return Results.Json(new
            {
                active,
                nodes = TenantTree.StandableNodes(tam.Db, accountId)
                    .Select(n => new { id = n.Id, display = n.Display }),
            }, TamJson.Options);
        });

        app.MapGet("/openapi.json", (HttpContext http) => OpenApiEndpoint.Handle(http, model));

        return app;
    }

    /// <summary>The one request-culture resolver — explicit ?culture, else Accept-Language,
    /// validated against the model's catalogues, else the default. Shared by the API pipeline and
    /// the auth pages so an unknown culture can't slip past one of them (it did: the auth pages
    /// used to skip the whitelist).</summary>
    public static string ResolveCulture(HttpContext http, TamModel model)
    {
        var requested = http.Request.Query.TryGetValue("culture", out var q) && q.Count > 0
            ? q[0]
            : http.Request.Headers.AcceptLanguage.ToString().Split(',').FirstOrDefault()?.Split(';')[0].Trim();
        return requested is { Length: > 0 } &&
            model.Locales.Cultures.Contains(requested, StringComparer.OrdinalIgnoreCase)
                ? requested
                : model.DefaultCulture;
    }

    public static OperationContext BuildContext(HttpContext http, TamModel model)
    {
        var actor = http.RequestServices.GetRequiredService<IActorProvider>().GetActor(http);
        // The pinned ambient tenant — includes an act-as rebind (docs/26 D-H4), so the context,
        // the actor above and the DbContext filter all agree on the node this request acts in.
        var tenant = TamTenant.Resolve(http);
        var culture = ResolveCulture(http, model);

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

    /// <summary>ETag over the content revision AND the actor's permissions (which the manifest
    /// embeds), so two actors with different grants never share a cached manifest.</summary>
    private static string ManifestETag(long revision, IReadOnlySet<string> permissions)
    {
        var perms = string.Join(",", permissions.Order());
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(perms));
        return $"\"{revision:x}-{Convert.ToHexString(hash.AsSpan(0, 8))}\"";
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
