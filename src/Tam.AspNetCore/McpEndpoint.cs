using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Tam.AspNetCore;

/// <summary>
/// Minimal MCP server over streamable HTTP JSON-RPC (docs/11): every operation is a tool,
/// every form has a ".resolve" preflight tool, every view is a query tool. Agents run the
/// same pipeline as humans — same validation, same findings, same audit.
/// </summary>
public static class McpEndpoint
{
    public static async Task<IResult> Handle(HttpContext http, CancellationToken ct)
    {
        var model = http.RequestServices.GetRequiredService<TamModel>();
        var request = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, TamJson.Options, ct);

        var id = request.TryGetProperty("id", out var idEl) ? JsonNode.Parse(idEl.GetRawText()) : null;
        var method = request.TryGetProperty("method", out var m) ? m.GetString() : null;

        object? result = method switch
        {
            "initialize" => new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "tam", version = "0.1" },
            },
            "notifications/initialized" => null,
            "ping" => new { },
            "tools/list" => new { tools = await Tools(http, model, ct) },
            "tools/call" => await Call(http, model, request, ct),
            _ => null,
        };

        if (id is null) return Results.StatusCode(StatusCodes.Status202Accepted);
        return Results.Json(new { jsonrpc = "2.0", id, result }, TamJson.Options);
    }

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

        var tools = new List<object>();

        foreach (var (opId, op) in model.Operations)
        {
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
            tools.Add(new
            {
                name = ToolName(formId) + "_resolve",
                description = $"Preflight for {form.OperationId}: returns missing required fields, options, warnings and suggestions for a partial input.",
                inputSchema = Schema(model.Operations[form.OperationId].InputFields, Label, allOptional: true),
            });
        }

        foreach (var (viewId, view) in model.Views)
        {
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
        var @params = request.GetProperty("params");
        var name = @params.GetProperty("name").GetString()!;
        var args = @params.TryGetProperty("arguments", out var a) ? a : default;
        var context = TamAspNetCore.BuildContext(http, model);

        object? payload;
        if (name.StartsWith("views_", StringComparison.Ordinal) &&
            Match(model.Views.Keys, name["views_".Length..]) is { } viewId)
        {
            var executor = http.RequestServices.GetRequiredService<ViewExecutor>();
            var query = args.ValueKind == JsonValueKind.Object
                ? args.EnumerateObject().ToDictionary(p => p.Name, p => (string?)p.Value.ToString())
                : [];
            var (response, error) = await executor.ExecuteAsync(viewId, query, context, ct);
            payload = (object?)response ?? new { findings = new[] { model.Locales.Resolve(error!, context.Culture) } };
        }
        else if (name.EndsWith("_resolve", StringComparison.Ordinal) &&
            Match(model.Forms.Keys, name[..^"_resolve".Length]) is { } formId)
        {
            var executor = http.RequestServices.GetRequiredService<ResolveExecutor>();
            var (response, error) = await executor.ResolveAsync(
                formId, new ResolveExecutor.ResolveRequest(args, null, 0), context, ct);
            payload = (object?)response ?? new { findings = new[] { model.Locales.Resolve(error!, context.Culture) } };
        }
        else if (Match(model.Operations.Keys, name) is { } operationId)
        {
            var executor = http.RequestServices.GetRequiredService<OperationExecutor>();
            payload = await executor.ExecuteAsync(operationId, args, context, ct);
        }
        else
        {
            payload = new
            {
                findings = new[]
                {
                    model.Locales.Resolve(
                        PipelineFindings.UnknownOperation.With(("operation", name)), context.Culture),
                },
            };
        }

        var isError = payload is OperationResponse op &&
            op.Findings.Any(f => f.Severity == FindingSeverity.Error);
        return new
        {
            content = new[]
            {
                new { type = "text", text = JsonSerializer.Serialize(payload, TamJson.Options) },
            },
            isError,
        };
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
