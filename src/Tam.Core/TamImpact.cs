using System.Text;

namespace Tam;

/// <summary>
/// The Step 12 change-impact report (docs/tutorial/step-12): ONE build-time answer to "what
/// does this model change touch?" — computed by diffing the compiled model's manifest against
/// the committed baseline. Three severities:
///   ✓ surfaces that update THEMSELVES from the model (schemas, forms, grids, MCP, TS client),
///   ✗ D4-breaking changes (same classification the CI baseline gate enforces — the report
///     SHOWS what scripts/check_manifest.py will fail),
///   ! couplings to review: plugins gating a changed operation, subscribers of a changed
///     event, integrations mapping a changed operation, plugin view-contracts over a changed
///     view. Additive and invisible is not a thing here — if it changed, it is listed.
/// </summary>
public static class TamImpact
{
    public sealed record Report(IReadOnlyList<string> Lines, bool HasBreaks, bool HasChanges)
    {
        public string Format() => string.Join('\n', Lines);
    }

    public static Report Against(TamModel model, ManifestDto baseline)
    {
        var current = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), revision: 0);
        var lines = new List<string>();
        var breaks = new List<string>();

        DiffOperations(model, baseline, current, lines, breaks);
        DiffViews(model, baseline, current, lines, breaks);
        DiffEvents(baseline, current, lines);
        DiffKeys("form", baseline.Forms.Keys, current.Forms.Keys, lines, breaks);
        DiffKeys("grid", baseline.Grids.Keys, current.Grids.Keys, lines, breaks);
        DiffKeys("page", baseline.Pages.Keys, current.Pages.Keys, lines, breaks);

        var hasChanges = lines.Count > 0;
        if (!hasChanges)
        {
            lines.Add("no manifest changes vs baseline");
        }
        else if (breaks.Count > 0)
        {
            lines.Add("");
            lines.Add("✗ NON-ADDITIVE vs release baseline (D4) — CI fails until the new baseline "
                + "is consciously re-exported and committed:");
            lines.AddRange(breaks.Select(b => $"    ✗ {b}"));
        }
        else
        {
            lines.Add("");
            lines.Add("✓ additive-only — the committed baseline stays valid");
        }
        return new Report(lines, breaks.Count > 0, hasChanges);
    }

    private static void DiffOperations(
        TamModel model, ManifestDto baseline, ManifestDto current,
        List<string> lines, List<string> breaks)
    {
        foreach (var id in baseline.Operations.Keys.Except(current.Operations.Keys).Order())
        {
            lines.Add($"REMOVED operation {id}");
            breaks.Add($"operation {id} removed");
        }
        foreach (var id in current.Operations.Keys.Except(baseline.Operations.Keys).Order())
        {
            lines.Add($"ADDED operation {id}");
            AutoSurfaces(model, lines, formsOf(id), gridsActing: []);
            Couplings(model, current, id, lines);
        }
        foreach (var id in baseline.Operations.Keys.Intersect(current.Operations.Keys).Order())
        {
            var b = baseline.Operations[id];
            var c = current.Operations[id];
            var changed = new List<string>();
            if (b.Permission != c.Permission)
            {
                changed.Add($"permission '{b.Permission}' → '{c.Permission}'");
                breaks.Add($"operation {id}: permission changed");
            }
            DiffFields($"operation {id}", b.Fields, c.Fields, changed, breaks,
                newRequiredBreaks: true);
            if (changed.Count == 0) continue;

            lines.Add($"CHANGED operation {id}");
            lines.AddRange(changed.Select(x => $"    {x}"));
            AutoSurfaces(model, lines, formsOf(id), gridsActing: []);
            Couplings(model, current, id, lines);
        }

        List<string> formsOf(string operationId) => model.Forms.Values
            .Where(f => f.OperationId == operationId).Select(f => f.Id).Order().ToList();
    }

    private static void DiffViews(
        TamModel model, ManifestDto baseline, ManifestDto current,
        List<string> lines, List<string> breaks)
    {
        foreach (var id in baseline.Views.Keys.Except(current.Views.Keys).Order())
        {
            lines.Add($"REMOVED view {id}");
            breaks.Add($"view {id} removed");
        }
        foreach (var id in current.Views.Keys.Except(baseline.Views.Keys).Order())
            lines.Add($"ADDED view {id}");

        foreach (var id in baseline.Views.Keys.Intersect(current.Views.Keys).Order())
        {
            var b = baseline.Views[id];
            var c = current.Views[id];
            var changed = new List<string>();
            if (b.Permission != c.Permission)
            {
                changed.Add($"permission '{b.Permission}' → '{c.Permission}'");
                breaks.Add($"view {id}: permission changed");
            }
            DiffFields($"view {id} result", b.ResultFields, c.ResultFields, changed, breaks,
                newRequiredBreaks: false);
            DiffFields($"view {id} query", b.QueryFields, c.QueryFields, changed, breaks,
                newRequiredBreaks: true);
            if (changed.Count == 0) continue;

            lines.Add($"CHANGED view {id}");
            lines.AddRange(changed.Select(x => $"    {x}"));
            var grids = model.Grids.Values.Where(g => g.ViewId == id)
                .Select(g => g.Id).Order().ToList();
            AutoSurfaces(model, lines, forms: [], gridsActing: grids);

            // Plugin view-contracts (docs/31 RequiresView): the couplings a removed result
            // field actually breaks — named per plugin, not left for a runtime 500.
            var currentFields = c.ResultFields.Select(f => f.Name).ToHashSet();
            foreach (var req in model.ViewRequirements.Where(r => r.ViewId == id))
            {
                var missing = req.Fields.Where(f => !currentFields.Contains(f)).ToList();
                lines.Add(missing.Count > 0
                    ? $"    ✗ plugin '{req.PluginId}' requires field(s) no longer served: {string.Join(", ", missing)}"
                    : $"    ! plugin '{req.PluginId}' reads this view (declared contract: {string.Join(", ", req.Fields)})");
                if (missing.Count > 0)
                    breaks.Add($"view {id}: plugin '{req.PluginId}' contract broken ({string.Join(", ", missing)})");
            }
        }
    }

    private static void DiffEvents(ManifestDto baseline, ManifestDto current, List<string> lines)
    {
        foreach (var id in current.Events.Keys.Except(baseline.Events.Keys).Order())
            lines.Add($"ADDED event {id} ({string.Join(", ", current.Events[id].Fields)})");
        foreach (var id in baseline.Events.Keys.Intersect(current.Events.Keys).Order())
        {
            var added = current.Events[id].Fields.Except(baseline.Events[id].Fields).ToList();
            var removed = baseline.Events[id].Fields.Except(current.Events[id].Fields).ToList();
            if (added.Count == 0 && removed.Count == 0) continue;
            lines.Add($"CHANGED event {id}"
                + (added.Count > 0 ? $" +[{string.Join(", ", added)}]" : "")
                + (removed.Count > 0 ? $" -[{string.Join(", ", removed)}]" : ""));
            foreach (var plugin in current.Events[id].SubscribedBy)
                lines.Add($"    ! subscriber '{plugin}' consumes this payload — verify its reads");
        }
    }

    private static void DiffKeys(
        string kind, IEnumerable<string> baseline, IEnumerable<string> current,
        List<string> lines, List<string> breaks)
    {
        var b = baseline.ToHashSet();
        var c = current.ToHashSet();
        foreach (var id in b.Except(c).Order())
        {
            lines.Add($"REMOVED {kind} {id}");
            breaks.Add($"{kind} {id} removed");
        }
        foreach (var id in c.Except(b).Order())
            lines.Add($"ADDED {kind} {id}");
    }

    private static void DiffFields(
        string path, IReadOnlyList<ManifestField> baseline, IReadOnlyList<ManifestField> current,
        List<string> changed, List<string> breaks, bool newRequiredBreaks)
    {
        var b = baseline.ToDictionary(f => f.Name);
        var c = current.ToDictionary(f => f.Name);
        foreach (var (name, bf) in b)
        {
            if (!c.TryGetValue(name, out var cf))
            {
                changed.Add($"field {name} REMOVED");
                breaks.Add($"{path}: field '{name}' removed");
                continue;
            }
            if (bf.Type != cf.Type || bf.WireKind != cf.WireKind)
            {
                changed.Add($"field {name}: type '{bf.Type}/{bf.WireKind}' → '{cf.Type}/{cf.WireKind}'");
                breaks.Add($"{path}.{name}: type changed");
            }
            if (!bf.Required && cf.Required)
            {
                changed.Add($"field {name}: optional → REQUIRED");
                breaks.Add($"{path}.{name}: optional field became required");
            }
            if (bf.Lookup != cf.Lookup)
                changed.Add($"field {name}: lookup '{bf.Lookup ?? "-"}' → '{cf.Lookup ?? "-"}'");
        }
        foreach (var (name, cf) in c.Where(kv => !b.ContainsKey(kv.Key)))
        {
            changed.Add($"field {name} ADDED{(cf.Required ? " (required)" : "")}");
            if (cf.Required && newRequiredBreaks)
                breaks.Add($"{path}: new REQUIRED field '{name}' breaks existing callers");
        }
    }

    /// <summary>The green half of the report: everything that regenerates from the model with
    /// no work — stated explicitly so "nothing to do" is a claim, not an absence.</summary>
    private static void AutoSurfaces(
        TamModel model, List<string> lines, IReadOnlyList<string> forms, IReadOnlyList<string> gridsActing)
    {
        lines.Add("    ✓ HTTP endpoint + OpenAPI + MCP tool schema update from the model");
        if (forms.Count > 0)
            lines.Add($"    ✓ form(s) re-render from the manifest: {string.Join(", ", forms)}");
        if (gridsActing.Count > 0)
            lines.Add($"    ✓ grid(s) re-render from the manifest: {string.Join(", ", gridsActing)}");
        lines.Add("    ✓ TypeScript client: regenerate (scripts/generate-types.mjs) — CI baseline gate reminds");
    }

    private static void Couplings(TamModel model, ManifestDto current, string operationId, List<string> lines)
    {
        if (current.Operations.TryGetValue(operationId, out var op))
            foreach (var plugin in op.GatedBy)
                lines.Add($"    ! plugin '{plugin}' gates this operation — its gate sees the new input");
        foreach (var integration in model.Integrations.Values
            .Where(i => i.OperationId == operationId))
            lines.Add($"    ! integration '{integration.Id}' maps into this operation — verify its mapping");
    }
}
