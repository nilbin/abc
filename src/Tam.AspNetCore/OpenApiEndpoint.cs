using Microsoft.AspNetCore.Http;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// OpenAPI 3.1 document derived from the model (docs/03: "the framework derives OpenAPI").
/// Operations are POSTs with typed request bodies; views are GETs with query parameters.
/// Descriptions resolve from the locale catalogs in the request culture.
/// </summary>
public static class OpenApiEndpoint
{
    public static async Task<IResult> Handle(HttpContext http, TamModel model)
    {
        var context = TamAspNetCore.BuildContext(http, model);
        string Label(string key) => model.Locales.Lookup(key, context.Culture) ?? key;

        // Per-tenant document (docs/22): inactive plugins' paths are omitted.
        var active = http.RequestServices.GetService(typeof(ITamDb)) is ITamDb tam
            ? await PluginActivations.ActiveAsync(tam.Db, context.TenantId.Value, http.RequestAborted)
            : new HashSet<string>();
        bool Included(string? plugin) => plugin is null || active.Contains(plugin);

        var paths = new Dictionary<string, object>();

        foreach (var (id, operation) in model.Operations)
        {
            if (!Included(operation.Plugin)) continue;
            paths[$"/api/operations/{id}"] = new
            {
                post = new
                {
                    operationId = id,
                    summary = Label(operation.TitleKey),
                    tags = new[] { id.Split('.')[0] },
                    description = $"Requires permission '{operation.Permission}'.",
                    parameters = CommonParameters(includeIdempotency: true),
                    requestBody = new
                    {
                        required = true,
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new { schema = InputSchema(operation, Label) },
                        },
                    },
                    responses = OperationResponses(),
                },
            };
        }

        foreach (var (id, view) in model.Views)
        {
            if (!Included(view.Plugin)) continue;
            var parameters = CommonParameters(includeIdempotency: false)
                .Concat(view.QueryFields.Select(field => (object)new
                {
                    name = field.WireName,
                    @in = "query",
                    required = false,
                    description = Label(field.LabelKey),
                    schema = FieldSchema(field),
                }))
                .Concat(new object[]
                {
                    new { name = "page", @in = "query", schema = new { type = "integer" } },
                    new { name = "pageSize", @in = "query", schema = new { type = "integer" } },
                    new { name = "sort", @in = "query", schema = new { type = "string", @enum = view.Capabilities.Sortable } },
                    new { name = "dir", @in = "query", schema = new { type = "string", @enum = new[] { "asc", "desc" } } },
                })
                .ToArray();

            paths[$"/api/views/{id}"] = new
            {
                get = new
                {
                    operationId = $"views.{id}",
                    summary = id,
                    tags = new[] { id.Split('.')[0] },
                    description = $"Requires permission '{view.Permission}'.",
                    parameters,
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = new
                        {
                            description = "Rows page",
                            content = new Dictionary<string, object>
                            {
                                ["application/json"] = new
                                {
                                    schema = new
                                    {
                                        type = "object",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["rows"] = new
                                            {
                                                type = "array",
                                                items = RecordSchema(view.ResultFields, Label),
                                            },
                                            ["total"] = new { type = "integer" },
                                            ["page"] = new { type = "integer" },
                                            ["pageSize"] = new { type = "integer" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };
        }

        var document = new
        {
            openapi = "3.1.0",
            info = new
            {
                title = Label("app.title"),
                version = "1",
                description = "Derived from the Tam application model. Every mutation is an operation; every read is a view.",
            },
            paths,
            components = new
            {
                schemas = new Dictionary<string, object>
                {
                    ["Finding"] = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["code"] = new { type = "string" },
                            ["severity"] = new { type = "string", @enum = new[] { "information", "warning", "error" } },
                            ["args"] = new { type = "object" },
                            ["targets"] = new { type = "array", items = new { type = "string" } },
                            ["blocksSubmission"] = new { type = "boolean" },
                            ["message"] = new { type = "string" },
                        },
                    },
                    ["OperationResponse"] = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["output"] = new { type = "object" },
                            ["findings"] = new { type = "array", items = new { @ref = "#/components/schemas/Finding" } },
                            ["effects"] = new { type = "array", items = new { type = "object" } },
                            ["newVersion"] = new { type = "integer" },
                            ["auditReference"] = new { type = "string" },
                            ["conflicts"] = new { type = "array", items = new { type = "object" } },
                        },
                    },
                },
            },
        };

        // "$ref"/"$in" need literal names System.Text.Json can't express via anonymous types ("@ref" → "ref").
        var json = System.Text.Json.JsonSerializer.Serialize(document, TamJson.Options)
            .Replace("\"ref\":", "\"$ref\":");
        return Results.Text(json, "application/json");
    }

    private static object[] CommonParameters(bool includeIdempotency)
    {
        var parameters = new List<object>
        {
            new { name = "culture", @in = "query", schema = new { type = "string" } },
        };
        if (includeIdempotency)
        {
            parameters.Add(new
            {
                name = "X-Idempotency-Key",
                @in = "header",
                schema = new { type = "string" },
                description = "Replays the stored response for a repeated identical request; rejects reuse with a different payload.",
            });
        }
        return [.. parameters];
    }

    private static Dictionary<string, object> OperationResponses() => new()
    {
        ["200"] = Response("Success"),
        ["403"] = Response("Not authorized"),
        ["409"] = Response("Field conflicts (three-way merge)"),
        ["422"] = Response("Validation findings"),
    };

    private static object Response(string description) => new
    {
        description,
        content = new Dictionary<string, object>
        {
            ["application/json"] = new { schema = new { @ref = "#/components/schemas/OperationResponse" } },
        },
    };

    private static object InputSchema(OperationDefinition operation, Func<string, string> label) =>
        RecordSchema(operation.InputFields, label,
            operation.InputFields.Where(f => f.Required).Select(f => f.WireName).ToArray());

    private static object RecordSchema(
        IReadOnlyList<FieldModel> fields, Func<string, string> label, string[]? required = null)
    {
        var properties = fields.ToDictionary(
            f => f.WireName,
            f => (object)FieldSchema(f, label(f.LabelKey)));
        return required is { Length: > 0 }
            ? new { type = "object", properties, required }
            : new { type = "object", properties };
    }

    private static object FieldSchema(FieldModel field, string? description = null)
    {
        var type = SemanticType.JsonType(field.Semantic.WireKind);
        if (field.IsChangeSet)
        {
            return new
            {
                type = "object",
                description = description is null ? "Change set" : $"{description} (change set)",
                properties = new Dictionary<string, object>
                {
                    ["original"] = new { type },
                    ["value"] = new { type },
                },
            };
        }
        return field.EnumOptions is { Count: > 0 } options
            ? new { type, description, @enum = options.Select(Naming.Camel).ToArray() }
            : (object)new { type, description, maxLength = field.Semantic.MaxLength };
    }
}
