using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// Minimal MCP server over streamable HTTP JSON-RPC (docs/11): every operation is a tool,
/// every form has a ".resolve" preflight tool, every view is a query tool. Agents run the
/// same pipeline as humans — same validation, same findings, same audit.
/// </summary>
public static class McpEndpoint
{
    // A process-lifetime empty JSON object, the stand-in for an omitted tools/call "arguments".
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    public static async Task<IResult> Handle(HttpContext http, CancellationToken ct)
    {
        var model = http.RequestServices.GetRequiredService<TamModel>();

        JsonElement request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, TamJson.Options, ct);
        }
        catch (JsonException)
        {
            // Malformed body: JSON-RPC parse error, not an unhandled 500 (Sol review, Finding 6C).
            return JsonRpcError(null, -32700, "Parse error");
        }

        var id = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var idEl)
            ? JsonNode.Parse(idEl.GetRawText()) : null;
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("method", out var m) || m.GetString() is not { } method)
            return JsonRpcError(id, -32600, "Invalid request");

        object? result;
        try
        {
            switch (method)
            {
                case "initialize":
                    result = new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "tam", version = "0.1" },
                    };
                    break;
                case "notifications/initialized": result = null; break;
                case "ping": result = new { }; break;
                case "tools/list": result = new { tools = await Tools(http, model, ct) }; break;
                case "tools/call": result = await Call(http, model, request, ct); break;
                default:
                    // Unknown method → -32601, not a success-shaped null result (Finding 6D).
                    return JsonRpcError(id, -32601, $"Method not found: {method}");
            }
        }
        catch (JsonRpcException ex)
        {
            return JsonRpcError(id, ex.Code, ex.Message);
        }

        if (id is null) return Results.StatusCode(StatusCodes.Status202Accepted);
        return Results.Json(new { jsonrpc = "2.0", id, result }, TamJson.Options);
    }

    private static IResult JsonRpcError(JsonNode? id, int code, string message) =>
        Results.Json(new { jsonrpc = "2.0", id, error = new { code, message } }, TamJson.Options);

    private static async Task<List<object>> Tools(HttpContext http, TamModel model, CancellationToken ct)
    {
        var context = TamAspNetCore.BuildContext(http, model);
        var culture = context.Culture;
        string Label(string key) => model.Locales.Lookup(key, culture) ?? key;

        // Per-tenant schemas (docs/15): agents see the tenant's custom fields with the
        // admin-authored labels and descriptions — the same words the tenant's humans read.
        var registry = http.RequestServices.GetService(typeof(IExtensionRegistry)) as IExtensionRegistry;
        var overlay = registry is null
            ? new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>()
            : await registry.All(context.TenantId, ct);

        // Inactive plugins' tools are not advertised (docs/22); calls to them are gated by
        // the executors either way, so this is discovery hygiene, not the security boundary.
        var active = http.RequestServices.GetService(typeof(ITamDb)) is ITamDb tam
            ? await PluginActivations.ActiveAsync(tam.Db, context.TenantId.Value, ct)
            : new HashSet<string>();
        bool Included(string? plugin) => plugin is null || active.Contains(plugin);

        // Discovery is permission-filtered (Sol review, Finding 6A): an actor only sees tools it
        // may actually run — the executor is still the boundary, but discovery must not leak the
        // names, descriptions and (custom-field) schemas of operations/views the actor can't use.
        var tools = new List<object>();

        foreach (var (opId, op) in model.Operations)
        {
            if (!Included(op.Plugin) || !context.Actor.Can(op.Permission)) continue;
            var extensible = op.ExtensibleEntity is { } entity
                ? overlay.GetValueOrDefault(TamModel.EntityKey(entity))
                : null;
            tools.Add(new
            {
                name = ToolName(opId),
                description = Label(op.TitleKey),
                inputSchema = Schema(op.InputFields, Label, allOptional: false, extensible, culture),
            });
        }

        foreach (var (formId, form) in model.Forms)
        {
            if (!Included(form.Plugin)) continue;
            var formOp = model.Operations[form.OperationId];
            if (!context.Actor.Can(formOp.Permission)) continue;
            // The resolve preflight advertises the SAME effective input as the operation —
            // including tenant custom fields (Sol review, Finding 6E): an agent that can see a
            // custom field on the operation must be able to preflight it.
            var formExtensible = formOp.ExtensibleEntity is { } formEntity
                ? overlay.GetValueOrDefault(TamModel.EntityKey(formEntity))
                : null;
            tools.Add(new
            {
                name = ToolName(formId) + "_resolve",
                description = $"Preflight for {form.OperationId}: returns missing required fields, options, warnings and suggestions for a partial input.",
                inputSchema = Schema(formOp.InputFields, Label, allOptional: true, formExtensible, culture),
            });
        }

        foreach (var (viewId, view) in model.Views)
        {
            if (!Included(view.Plugin) || !context.Actor.Can(view.Permission)) continue;
            tools.Add(new
            {
                name = ToolName("views." + viewId),
                description = $"Query the {viewId} read model.",
                inputSchema = Schema(view.QueryFields, Label, allOptional: true),
            });
        }

        return tools;
    }

    private static async Task<object> Call(
        HttpContext http, TamModel model, JsonElement request, CancellationToken ct)
    {
        // params must be an object before any property lookup — a JSON null or array would make
        // TryGetProperty throw InvalidOperationException, which escapes the JSON-RPC error path
        // as a 500 (Sol re-review, MCP A).
        if (request.TryGetProperty("params", out var @params) is false
            || @params.ValueKind != JsonValueKind.Object
            || @params.TryGetProperty("name", out var nameEl) is false
            || nameEl.GetString() is not { } name)
            throw new JsonRpcException(-32602, "Invalid params: expected an object with a tool name");
        // A missing/undefined "arguments" is a valid no-arg call — substitute an empty object, never
        // a default (Undefined) JsonElement, which would throw at GetRawText downstream (MCP B).
        var args = @params.TryGetProperty("arguments", out var a) && a.ValueKind != JsonValueKind.Undefined
            ? a
            : EmptyObject;
        var context = TamAspNetCore.BuildContext(http, model);

        object? payload;
        bool errored;
        if (name.StartsWith("views_", StringComparison.Ordinal) &&
            Match(model.Views.Keys, name["views_".Length..]) is { } viewId)
        {
            var executor = http.RequestServices.GetRequiredService<ViewExecutor>();
            var query = args.ValueKind == JsonValueKind.Object
                ? args.EnumerateObject().ToDictionary(p => p.Name, p => (string?)p.Value.ToString())
                : [];
            var (response, error) = await executor.ExecuteAsync(viewId, query, context, ct);
            errored = response is null;
            payload = (object?)response ?? new { findings = new[] { model.Locales.Resolve(error!, context.Culture) } };
        }
        else if (name.EndsWith("_resolve", StringComparison.Ordinal) &&
            Match(model.Forms.Keys, name[..^"_resolve".Length]) is { } formId)
        {
            var executor = http.RequestServices.GetRequiredService<ResolveExecutor>();
            var (response, error) = await executor.ResolveAsync(
                formId, new ResolveExecutor.ResolveRequest(args, null, 0), context, ct);
            errored = response is null;
            payload = (object?)response ?? new { findings = new[] { model.Locales.Resolve(error!, context.Culture) } };
        }
        else if (Match(model.Operations.Keys, name) is { } operationId)
        {
            var executor = http.RequestServices.GetRequiredService<OperationExecutor>();
            var response = await executor.ExecuteAsync(operationId, args, context, ct);
            errored = response.Findings.Any(f => f.Severity == FindingSeverity.Error);
            payload = response;
        }
        else
        {
            errored = true;
            payload = new
            {
                findings = new[]
                {
                    model.Locales.Resolve(
                        PipelineFindings.UnknownOperation.With(("operation", name)), context.Culture),
                },
            };
        }

        // isError reflects ANY tool's failure — operation, view, resolve or unknown-tool — not
        // just an OperationResponse's error findings (Sol review, Finding 6B): an unauthorized
        // view or a bad query must not read as isError:false to the agent.
        return new
        {
            content = new[]
            {
                new { type = "text", text = JsonSerializer.Serialize(payload, TamJson.Options) },
            },
            isError = errored,
        };
    }

    /// <summary>A JSON-RPC protocol error raised from tool handling (Sol review, Finding 6C):
    /// caught at <see cref="Handle"/> and returned as a spec `error` object.</summary>
    private sealed class JsonRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static object Schema(
        IReadOnlyList<FieldModel> fields,
        Func<string, string> label,
        bool allOptional = false,
        IReadOnlyList<ExtensionFieldSpec>? extensions = null,
        string culture = "en")
    {
        var properties = new Dictionary<string, object>();
        foreach (var field in fields)
        {
            var schemaType = SemanticType.JsonType(field.Semantic.WireKind);
            properties[field.WireName] = field.EnumOptions is { Count: > 0 } options
                // Wire enums are camelCase (TamJson): advertise values the deserializer accepts.
                ? new { type = schemaType, description = label(field.LabelKey), @enum = options.Select(Naming.Camel).ToArray() }
                : (object)new { type = schemaType, description = label(field.LabelKey) };
        }

        if (extensions is { Count: > 0 })
        {
            var extensionProperties = new Dictionary<string, object>();
            foreach (var spec in extensions.Where(s => s.State == ExtensionFieldState.Active))
            {
                var text = spec.Labels.GetValueOrDefault(culture) ?? spec.Labels.Values.FirstOrDefault() ?? spec.Key;
                var description = spec.Descriptions?.GetValueOrDefault(culture)
                    ?? spec.Descriptions?.Values.FirstOrDefault();
                extensionProperties[spec.Key] = new
                {
                    type = "object",
                    description = description is null ? text : $"{text}. {description}",
                    properties = new
                    {
                        original = new { type = SemanticType.JsonType(spec.Semantic.WireKind) },
                        value = new { type = SemanticType.JsonType(spec.Semantic.WireKind) },
                    },
                };
            }
            properties["extensions"] = new
            {
                type = "object",
                description = "Tenant-defined custom fields as change sets ({original, value} per key).",
                properties = extensionProperties,
            };
        }

        return new
        {
            type = "object",
            properties,
            required = allOptional
                ? []
                : fields.Where(f => f.Required).Select(f => f.WireName).ToArray(),
        };
    }



    // MCP tool names allow [a-zA-Z0-9_-]; operation ids use dots + kebab, so the reverse
    // mapping is a lookup against known ids, never string surgery.
    private static string ToolName(string id) => id.Replace('.', '_').Replace('-', '_');

    private static string? Match(IEnumerable<string> ids, string toolName) =>
        ids.FirstOrDefault(id => ToolName(id) == toolName);
}
