using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static partial class TamAspNetCore
{
    public static WebApplication MapTam(this WebApplication app)
    {
        var model = app.Services.GetRequiredService<TamModel>();

        // Model hygiene warnings (e.g. L10N005 wrapper-label collisions) surface once at startup —
        // advisory by design: Build() already threw on everything that must block.
        foreach (var warning in model.Warnings)
            app.Logger.LogWarning("{TamModelWarning}", warning);

        app.MapPost("/api/operations/{operationId}", async (
            string operationId, HttpContext http, OperationExecutor executor, CancellationToken ct) =>
        {
            var context = BuildContext(http, model);
            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, TamJson.Options, ct);
            }
            catch (JsonException)
            {
                // A malformed or empty body is a client error — answer the same invalid-input
                // findings envelope the resolve/integration/MCP endpoints give, never a 500
                // (Sol re-review, boundary A).
                return FindingsResult(model, context,
                    PipelineFindings.InvalidInput.Create(), StatusCodes.Status400BadRequest);
            }
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
                ? FindingsResult(model, context, error)
                : Results.Json(response, TamJson.Options);
        });

        app.MapPost("/api/forms/{formId}/resolve", async (
            string formId, HttpContext http, ResolveExecutor executor, CancellationToken ct) =>
        {
            var context = BuildContext(http, model);
            // The body is an ENVELOPE, not the bare input — posting flat input is the most
            // common raw-wire mistake (docs/34 M3 gap 1), so it answers 400 naming the
            // expected shape, never a 500 with a serializer stack.
            ResolveExecutor.ResolveRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<ResolveExecutor.ResolveRequest>(
                    http.Request.Body, TamJson.Options, ct);
            }
            catch (JsonException)
            {
                request = null;
            }
            if (request is null || request.Input.ValueKind is not JsonValueKind.Object)
                return FindingsResult(model, context,
                    PipelineFindings.InvalidInput.With(
                        ("expected", """{ "input": { ...form fields... }, "changed": ["field"], "revision": 1 }""")),
                    StatusCodes.Status400BadRequest);
            var (response, error) = await executor.ResolveAsync(formId, request, context, ct);
            return error is not null
                ? FindingsResult(model, context, error)
                : Results.Json(response, TamJson.Options);
        });

        // Server-side memo of the SERIALIZED manifest, keyed by the same ETag clients cache on.
        // The manifest is a pure function of the ETag's inputs (content revision × permission
        // set), so a hit is exact; per-app closure so parallel hosts/tests never share entries.
        var manifestMemo = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();

        app.MapGet("/api/manifest", async (
            HttpContext http, IExtensionRegistry registry, ITamDb tam, CancellationToken ct) =>
        {
            var context = BuildContext(http, model);
            var overlay = await registry.All(context.TenantId, ct);
            var activePlugins = await ActivationCache.ForAsync(http.RequestServices, tam.Db, context.TenantId.Value, ct);
            // The tenant's nav overrides (docs/30 v2) join the manifest inputs — and the
            // revision below, or clients would keep the pre-override tree cached.
            var navOverrides = await tam.Db.Set<NavOverrideEntity>()
                .OrderBy(x => x.NodeId).ToListAsync(ct);
            var revision = OverlayRevision(overlay, activePlugins, NavOverlay.Fingerprint(navOverrides));

            // The manifest is a pure function of (model, overlay, activePlugins, actor permissions).
            // The overlay+activation queries are cheap; the reflection rebuild + serialization are
            // not — so serve a content ETag and answer 304 when the client already has this version,
            // skipping the build entirely (review-round-3 #5).
            var etag = ManifestETag(revision, context.Actor.Permissions);
            http.Response.Headers["ETag"] = etag;
            if (http.Request.Headers.IfNoneMatch.ToString() == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);

            // Review-round-4 #4: clients without the cached copy (agents, new tabs, cold loads)
            // used to pay the reflective rebuild every request — now only the FIRST request per
            // (content, permission-set) version does.
            if (!manifestMemo.TryGetValue(etag, out var body))
            {
                var manifest = ManifestBuilder.Build(model, overlay, revision: revision, activePlugins);
                // Read masking (docs/27 D-A3): sensitive fields the actor may not see are absent
                // from the manifest entirely. Deterministic in the actor's permission set, so the
                // ETag (and this memo) hold.
                manifest = ManifestBuilder.MaskSensitive(manifest, context.Actor) with
                {
                    Catalogs = MergeExtensionLabels(manifest.Catalogs, overlay, model),
                    ActorPermissions = context.Actor.Permissions.ToList(),
                };
                manifest = NavOverlay.Apply(manifest, navOverrides);
                body = JsonSerializer.SerializeToUtf8Bytes(manifest, TamJson.Options);
                // Crude bound: stale versions accumulate one entry per content/permission flip —
                // reset rather than LRU-track; the next requests repopulate the live versions.
                if (manifestMemo.Count >= 128) manifestMemo.Clear();
                manifestMemo[etag] = body;
            }
            return Results.Bytes(body, "application/json");
        });

        // Plugin-shipped inbound integrations (docs/22): activation-gated, inbox-idempotent.
        app.MapPost("/api/integrations/{integrationId}", async (
            string integrationId, HttpContext http, ITamDb tam, CancellationToken ct) =>
        {
            if (!model.Integrations.TryGetValue(integrationId, out var integration))
                return Results.NotFound();

            var context = BuildContext(http, model);
            if (!await ActivationCache.ContributionExistsAsync(
                    http.RequestServices, tam.Db, integration.PluginId, context.TenantId.Value, ct))
                return Results.NotFound();   // inactive plugin → the integration does not exist

            JsonElement payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, TamJson.Options, ct);
            }
            catch (JsonException)
            {
                // A partner posting malformed JSON is a client error, not a server fault.
                return FindingsResult(model, context, OutboundFindings.MalformedPayload.Create());
            }
            // Well-formed JSON but not an array of rows: tell the partner its payload SHAPE was
            // wrong, rather than answering 200 with an empty result set that reads as "accepted,
            // nothing to do" (Sol re-review, boundary B).
            if (payload.ValueKind != JsonValueKind.Array)
                return FindingsResult(model, context,
                    OutboundFindings.ExpectedArray.Create(), StatusCodes.Status400BadRequest);
            var results = await PluginIntegrationRunner.RunAsync(integration, payload, context, tam.Db, ct);
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

        // Document CONTENT download (docs/35): metadata rides the views; bytes ride this one
        // streaming endpoint. Authorization mirrors the views exactly — the capability atom,
        // then the SAME visibility predicate the listings filter by; an out-of-reach or
        // retired document 404s (no existence leak).
        app.MapGet("/api/documents/{id:guid}/content", async (
            Guid id, HttpContext http, ITamDb tam, ReachResolver reach, IDocumentStore store,
            CancellationToken ct) =>
        {
            var context = BuildContext(http, model);
            if (!context.Actor.Can("documents.read"))
                return FindingsResult(model, context,
                    PipelineFindings.NotAuthorized.With(("permission", "documents.read")));
            var document = await tam.Db.Set<DocumentEntity>()
                .SingleOrDefaultAsync(d => d.Id == id && !d.Retired, ct);
            if (document is null) return Results.NotFound();
            var visible = await DocumentAccess.VisibleFolderIdsAsync(tam, reach, context, ct);
            if (!visible.Contains(document.FolderId)) return Results.NotFound();
            var content = await store.OpenAsync(document.ContentHash, ct);
            return content is null
                ? Results.NotFound()
                : Results.Stream(content, document.ContentType, document.FileName);
        });

        // Document CONTENT staging (docs/36 streaming uploads): bytes arrive HERE as
        // multipart — no base64 overhead, no JSON-body bound — and are content-addressed
        // into the store. The WRITE stays an intent: documents.upload references the
        // returned hash and rides the full pipeline (authorization, folder visibility,
        // audit, idempotency). An unused staged blob is inert data (content addressing
        // dedupes; sweeping unreferenced blobs is the retention janitor's seam).
        app.MapPost("/api/documents/staging", async (
            HttpContext http, ITamDb tam, IDocumentStore store, CancellationToken ct) =>
        {
            var context = BuildContext(http, model);
            if (!context.Actor.Can("documents.add"))
                return FindingsResult(model, context,
                    PipelineFindings.NotAuthorized.With(("permission", "documents.add")));
            if (!http.Request.HasFormContentType) return Results.BadRequest();
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0 || file.Length > UploadDocument.StagedMaxBytes)
                return Results.BadRequest();
            using var buffer = new MemoryStream();
            await file.OpenReadStream().CopyToAsync(buffer, ct);
            var hash = await store.PutAsync(buffer.ToArray(), ct);
            // Staging is its own transaction — the blob row must exist before the intent
            // that references it arrives.
            await tam.Db.SaveChangesAsync(ct);
            return Results.Json(new { contentHash = hash, size = file.Length }, TamJson.Options);
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
        // Authorization fails fast in the pipeline, so an error response carries one code family;
        // the first error finding decides through the same mapping every endpoint uses.
        var error = response.Findings.FirstOrDefault(f => f.Severity == FindingSeverity.Error);
        return error is null ? StatusCodes.Status200OK : FindingStatus(error);
    }

    /// <summary>Findings answer through ONE door (docs/03): resolved via the locale catalogs,
    /// shaped as the universal envelope, mapped to a status by the one rule below — never a
    /// hand-built anonymous object at an endpoint.</summary>
    private static IResult FindingsResult(
        TamModel model, OperationContext context, Finding error, int? status = null) =>
        Results.Json(new { findings = new[] { model.Locales.Resolve(error, context.Culture) } },
            TamJson.Options, statusCode: status ?? FindingStatus(error));

    private static int FindingStatus(Finding error) =>
        error.Code == PipelineFindings.NotAuthorized.Code ? StatusCodes.Status403Forbidden
        : error.Code == PipelineFindings.UnknownView.Code
            || error.Code == PipelineFindings.UnknownOperation.Code
            || error.Code == PipelineFindings.UnknownForm.Code ? StatusCodes.Status404NotFound
        : StatusCodes.Status422UnprocessableEntity;

    /// <summary>Content-derived revision: any overlay change (add, retire, relabel) or plugin
    /// activation flip moves it, so clients know their manifest is stale.</summary>
    private static long OverlayRevision(
        IReadOnlyDictionary<string, IReadOnlyList<ExtensionFieldSpec>> overlay,
        IReadOnlySet<string>? activePlugins = null,
        string navFingerprint = "")
    {
        var fingerprint = string.Join("|", overlay
            .OrderBy(kv => kv.Key)
            .SelectMany(kv => kv.Value.Select(s => $"{kv.Key}/{s.Key}/{s.Type}/{s.State}/{s.Required}/{s.MaxLength}")))
            + "||" + string.Join(",", (activePlugins ?? new HashSet<string>()).Order())
            + "||" + navFingerprint;
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
                catalog[LabelKeys.Extension(spec.Key)] = label;
                var description = spec.Descriptions?.GetValueOrDefault(culture)
                    ?? spec.Descriptions?.GetValueOrDefault(model.DefaultCulture);
                if (description is not null)
                    catalog[LabelKeys.ExtensionDescription(spec.Key)] = description;
            }
        }

        return merged.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)kv.Value);
    }
}
