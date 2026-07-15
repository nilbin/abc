using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// Resolves the request's active tenant from the bearer token's active-tenant claim (docs/26): a
/// PKCE token names the tenant the account chose at login, and the account's membership in it is
/// re-checked per request (<see cref="ClaimsActorProvider"/>) — so the claim selects context, it
/// doesn't grant access. Falls back to <paramref name="fallback"/> for unauthenticated requests
/// (the interactive login/token endpoints, static files) where no tenant is named yet.
/// </summary>
public sealed class ClaimTenantProvider(string fallback) : ITenantProvider
{
    public TenantId GetTenant(HttpContext http)
    {
        var tenant = http.User.FindFirst(ClaimsActorProvider.ActiveTenantClaim)?.Value;
        return new TenantId(string.IsNullOrEmpty(tenant) ? fallback : tenant);
    }
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
        var db = http.RequestServices.GetRequiredService<ITamDb>().Db;

        // The global query filter already scopes the role lookup to the ambient tenant.
        var role = db.Set<RoleEntity>().FirstOrDefault(
            x => x.Name == name);

        return new Actor(
            name,
            displayName?.Invoke(name) ?? name,
            role?.Permissions() ?? new HashSet<string>());
    }
}

public static class TamAspNetCore
{
    public static IServiceCollection AddTam<TDbContext>(
        this IServiceCollection services, TamModel model,
        Action<TamIntegrationOptions>? configureIntegrations = null)
        where TDbContext : DbContext
    {
        services.AddSingleton(model);
        services.AddScoped(sp => new OperationExecutor(model, sp, s => s.GetRequiredService<TDbContext>()));
        services.AddScoped(sp => new ViewExecutor(model, sp));
        services.AddScoped(sp => new ResolveExecutor(model, sp.GetRequiredService<OperationExecutor>(), sp));
        services.AddScoped<IExtensionRegistry>(sp => new PluginAwareExtensionRegistry(
            new EfExtensionRegistry(sp.GetRequiredService<TDbContext>()), model, sp.GetRequiredService<TDbContext>(), sp));
        services.AddScoped<ITamDb>(sp => new TamDb(sp.GetRequiredService<TDbContext>()));
        // Plugin handler construction (gates, effect handlers, parked work): ctor injection from
        // the resolving scope — request scope for gates, tenant-pinned scopes for the rest.
        services.AddScoped<ITamActivator, TamActivator>();
        // People lookups for plugins (assign/notify): the sanctioned seam over identity tables.
        services.AddScoped<ITamDirectory, TamDirectory>();
        // Sanctioned envelope replay (docs/28 approvals seam 3): singleton because it always
        // executes in a fresh pinned scope of its own — never the caller's (whose transaction
        // may be the very one the parked envelope must be independent of).
        services.AddSingleton(sp => new EnvelopeReplay(model, sp, s => s.GetRequiredService<TDbContext>()));
        // Ambient tenant for the EF global query filter (docs: tenant isolation is enforced once at
        // the model, not re-filtered at every call site). Set per request by UseTamTenantScope.
        services.AddScoped<TenantScope>();
        // Request-scoped memoization of plugin activation (read 3-4× per request across existence,
        // gate, overlay and manifest) — collapses those to one query and keeps them coherent.
        services.AddScoped<ActivationCache>();

        // Outbound email seam (invites): TryAdd so a deployment's real transport wins when
        // registered first; the default logs the message — the dev inbox.
        services.TryAddSingleton<ITamEmail, LogTamEmail>();
        services.AddHttpContextAccessor();   // invite links derive their origin from the request

        // Secrets vault (docs/25): ASP.NET Data Protection encrypts at rest. The key ring is
        // persisted in the shared database (via the app's DbContext when it implements
        // IDataProtectionKeyContext) so it survives restarts and is shared across instances —
        // otherwise the default ephemeral ring orphans every stored secret on redeploy. A stable
        // application name keys the ring; production may wrap it with Azure KV / AWS KMS.
        var dp = services.AddDataProtection().SetApplicationName("Tam");
        // AddTam is unconstrained, so persist-to-DbContext (which needs TContext :
        // IDataProtectionKeyContext) is invoked reflectively when the app opts in by
        // implementing the interface. Apps that don't get the platform default and a warning.
        if (typeof(Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.IDataProtectionKeyContext)
            .IsAssignableFrom(typeof(TDbContext)))
        {
            typeof(Microsoft.AspNetCore.DataProtection.EntityFrameworkCoreDataProtectionExtensions)
                .GetMethod(nameof(Microsoft.AspNetCore.DataProtection
                    .EntityFrameworkCoreDataProtectionExtensions.PersistKeysToDbContext))!
                .MakeGenericMethod(typeof(TDbContext))
                .Invoke(null, [dp]);
        }
        services.AddScoped(sp => new SecretVault(
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            sp.GetRequiredService<ITamDb>()));

        // The outbound client's destination is tenant-controlled, so it is SSRF-guarded (blocks
        // private/link-local egress) and never follows redirects — a 302 could bounce a secret-bearing
        // request to an attacker host. A deployment reaching real internal targets opts in explicitly.
        var integrationOptions = new TamIntegrationOptions();
        configureIntegrations?.Invoke(integrationOptions);
        services.AddSingleton(integrationOptions);
        services.AddHttpClient("tam-integrations", c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = IntegrationEgress.Guard(integrationOptions),
            });

        // One retry policy shared by the inbound inbox and the outbound queue (docs/25): same
        // backoff, same dead-letter cap. The outbound retry driver drains failed pushes on its own
        // cadence; the inbox drains on the next inbound call but honours the same backoff gate.
        var retryPolicy = new RetryPolicy(
            integrationOptions.RetryBaseDelay, integrationOptions.RetryMaxDelay, integrationOptions.MaxAttempts);
        services.AddSingleton(retryPolicy);
        services.AddHostedService(sp => new IntegrationRetryDriver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
            model, retryPolicy, integrationOptions.RetryDriverInterval));

        // Housekeeping: trim completed transient history so the hot loops don't scan unbounded tables.
        services.AddHostedService(sp => new RetentionJanitor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
            integrationOptions));

        // Scheduler for outbound integrations (docs/25): one lightweight loop, no external deps.
        services.AddHostedService(sp => new IntegrationScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
            model));

        services.AddSingleton<IActorProvider, DevActorProvider>();
        services.AddSingleton<ITenantProvider>(new FixedTenantProvider("demo"));
        services.AddSingleton<EffectBroadcaster>();
        // Live-refresh backplane: in-process by default. A Postgres deployment registers the NOTIFY
        // adapter after AddTam (last registration wins) for cross-instance SSE (docs/12).
        services.AddSingleton<IEffectBackplane, LocalEffectBackplane>();
        services.AddHostedService(sp => new OutboxDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
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
