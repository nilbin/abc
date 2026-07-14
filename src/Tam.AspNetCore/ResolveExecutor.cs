using System.Text.Json;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// Batched form resolution (docs/05): evaluates portable rules and the server derivations
/// whose dependencies intersect the changed fields, and returns complete field state.
/// The same endpoint serves reactive web forms and MCP preflight (docs/11).
/// </summary>
public sealed class ResolveExecutor(TamModel model, OperationExecutor operations, IServiceProvider services)
{
    public sealed record ResolveRequest(JsonElement Input, string[]? Changed, long Revision);

    public async Task<(ResolveResponse? Response, Finding? Error)> ResolveAsync(
        string formId, ResolveRequest request, OperationContext context, CancellationToken ct)
    {
        if (!model.Forms.TryGetValue(formId, out var form))
            return (null, PipelineFindings.UnknownForm.With(("form", formId)));

        // Inactive plugin → the form does not exist for this tenant (docs/22).
        if (form.Plugin is { } plugin
            && services.GetService(typeof(ITamDb)) is ITamDb tam)
        {
            var active = await ActivationCache.ForAsync(services, tam.Db, context.TenantId.Value, ct);
            if (!active.Contains(plugin))
                return (null, PipelineFindings.UnknownForm.With(("form", formId)));
        }

        var operation = model.Operations[form.OperationId];
        if (!context.Actor.Can(operation.Permission))
            return (null, PipelineFindings.NotAuthorized.With(("permission", operation.Permission)));

        object input;
        try
        {
            input = request.Input.Deserialize(operation.InputType, TamJson.Options)!;
        }
        catch (JsonException)
        {
            return (null, PipelineFindings.InvalidInput.Create());
        }

        var merged = DerivationResult.Empty;
        foreach (var derivation in model.DerivationsFor(operation.InputType))
        {
            if (request.Changed is { Length: > 0 } changed &&
                derivation.DependsOn.Count > 0 &&
                !derivation.DependsOn.Intersect(changed).Any())
            {
                continue;
            }

            var args = operations.BindParameters(derivation.Method, input, context, ct);
            var invocation = derivation.Method.Invoke(null, args)!;
            var result = invocation is Task<DerivationResult> task
                ? await task
                : (DerivationResult)invocation;
            merged = merged.Merge(result);
        }

        object? FieldValue(string wireName)
        {
            var property = operation.InputType.GetProperties()
                .FirstOrDefault(p => Naming.Camel(p.Name) == wireName);
            return property?.GetValue(input);
        }

        var fields = new Dictionary<string, ResolvedFieldState>();
        foreach (var config in form.Fields)
        {
            var fieldModel = operation.InputFields.First(f => f.WireName == config.WireName);
            var visible = config.VisibleWhen is null || PxBinary.Truthy(config.VisibleWhen.Evaluate(FieldValue));
            var required = fieldModel.Required
                || (config.RequiredWhen is not null && PxBinary.Truthy(config.RequiredWhen.Evaluate(FieldValue)));

            fields[config.WireName] = new ResolvedFieldState(
                visible,
                Enabled: true,
                required,
                merged.Suggestions.GetValueOrDefault(config.WireName),
                merged.Options.GetValueOrDefault(config.WireName),
                merged.Findings
                    .Where(f => f.Targets.Any(t => t.Value == config.WireName))
                    .Select(f => model.Locales.Resolve(f, context.Culture))
                    .ToList());
        }

        var global = merged.Findings
            .Where(f => f.Targets.Count == 0)
            .Select(f => model.Locales.Resolve(f, context.Culture))
            .ToList();

        return (new ResolveResponse(fields, global, request.Revision), null);
    }
}
