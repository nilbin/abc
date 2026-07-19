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
        if (services.GetService(typeof(ITamDb)) is ITamDb tam
            && !await ActivationCache.ContributionExistsAsync(
                services, tam.Db, form.Plugin, context.TenantId.Value, ct))
            return (null, PipelineFindings.UnknownForm.With(("form", formId)));

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

        // The SAME operation-owned derivation run submit uses (docs/40): ALL derivations, every time,
        // so the complete field state resolve returns cannot report a requiredness or membership that
        // submit (which always runs them all) then contradicts (Sol re-review, Finding 4). request.
        // Changed no longer prunes the run; it now carries the change-membership signal a derivation
        // reads via DerivationContext.WasChanged (Sol re-review round 6, F3) — the fields the client
        // has touched, the resolve-time analogue of submit's present-field set. (A future delta
        // protocol would additionally use it to return only the changed field states.)
        var touched = request.Changed is { } changed
            ? changed.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var merged = await operations.RunDerivationsAsync(operation, input, context, ct, touched);

        // The ONE effective-value accessor for portable predicates (Sol re-review round 6, F2):
        // unwraps a Change<T> so an edit form's VisibleWhen/RequiredWhen sees the value, not the
        // change wrapper — resolve evaluates the exact shape submit's SelectedFormRequired does.
        object? FieldValue(string wireName) => OperationExecutor.EffectiveFieldValue(operation, input, wireName);

        var fields = new Dictionary<string, ResolvedFieldState>();
        foreach (var config in form.Fields)
        {
            var fieldModel = operation.InputFields.First(f => f.WireName == config.WireName);
            var visible = config.VisibleWhen is null || PxBinary.Truthy(config.VisibleWhen.Evaluate(FieldValue));
            var required = fieldModel.Required
                || (config.RequiredWhen is not null && PxBinary.Truthy(config.RequiredWhen.Evaluate(FieldValue)))
                // Operation-owned requiredness (docs/40): the field is required if any derivation's
                // Require() rule holds for it — the same authoritative rule submit enforces.
                || merged.Required.Any(r => r.When && r.Field == config.WireName);

            // The derivation's authoritative lookup binding (docs/40, Finding 6): the client opens
            // this View scoped by the contextual base filters — browsing the SAME candidate universe
            // submit validates against, so the derivation need not materialize it as inline options.
            var lookup = merged.Lookups.FirstOrDefault(l => l.Field == config.WireName);

            // Render the AUTHORITATIVE closed set's own options when one exists (Sol re-review round 5,
            // Finding 2), so the displayed set is exactly the enforced set — never an advisory
            // AddOptions list that could differ. Advisory options apply only when there is no closed
            // set (and DER008 forbids both on one field).
            var closedHere = merged.ClosedOptions.FirstOrDefault(c => c.Field == config.WireName);

            fields[config.WireName] = new ResolvedFieldState(
                visible,
                Enabled: true,
                required,
                merged.Suggestions.GetValueOrDefault(config.WireName),
                closedHere?.Options ?? merged.Options.GetValueOrDefault(config.WireName),
                lookup is null ? null : new ResolvedLookup(lookup.ViewId, lookup.Filters),
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
