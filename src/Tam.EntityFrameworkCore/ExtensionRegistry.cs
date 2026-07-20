using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Tam.EntityFrameworkCore;

public sealed class EfExtensionRegistry(DbContext db) : IExtensionRegistry
{
    public async Task<IReadOnlyList<ExtensionFieldSpec>> For(
        TenantId tenant, string entityKey, CancellationToken ct)
    {
        var rows = await db.Set<ExtensionFieldEntity>()
            .Where(x => x.Entity == entityKey)
            .ToListAsync(ct);
        return rows.Select(r => r.ToSpec()).ToList();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ExtensionFieldSpec>>> All(
        TenantId tenant, CancellationToken ct)
    {
        var rows = await db.Set<ExtensionFieldEntity>()
            .ToListAsync(ct);
        return rows.GroupBy(r => r.Entity).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ExtensionFieldSpec>)g.Select(r => r.ToSpec()).ToList());
    }
}

/// <summary>Wire shape of one entry in the "extensions" change channel.</summary>
public sealed record ExtensionChange(JsonElement? Original, JsonElement? Value);

public sealed record ExtensionApplyResult(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<FieldConflict> Conflicts,
    IReadOnlyList<string> AppliedKeys);

/// <summary>
/// Validates and applies tenant extension changes (docs/15): same Change semantics,
/// same three-way merge, same findings as compiled fields. Runs inside the operation transaction.
/// </summary>
public static class ExtensionApplier
{
    public static ExtensionApplyResult Apply(
        IExtensible entity,
        bool entityIsNew,
        IReadOnlyDictionary<string, ExtensionChange> changes,
        IReadOnlyList<ExtensionFieldSpec> specs)
    {
        var findings = new List<Finding>();
        var conflicts = new List<FieldConflict>();
        var applied = new List<string>();
        var byKey = specs.ToDictionary(s => s.Key, StringComparer.Ordinal);

        foreach (var (key, change) in changes)
        {
            if (!byKey.TryGetValue(key, out var spec))
            {
                findings.Add(ExtensionFindings.UnknownField.At(FieldPath.Extension(key)));
                continue;
            }
            if (spec.State is ExtensionFieldState.Retired or ExtensionFieldState.Draft)
            {
                findings.Add(ExtensionFindings.RetiredField.At(FieldPath.Extension(key)));
                continue;
            }
            if (spec.State is ExtensionFieldState.Deprecated)
            {
                findings.Add(ExtensionFindings.DeprecatedField.At(FieldPath.Extension(key)));
            }
            if (spec.ReadOnly)
            {
                // Plugin-owned state (docs/31 D-X2): only the owning plugin's writer sets it.
                findings.Add(ExtensionFindings.ReadOnlyField.At(FieldPath.Extension(key)));
                continue;
            }

            var submitted = FromElement(change.Value);
            var original = FromElement(change.Original);
            var current = entity.Extensions.Raw(key);
            var semantic = spec.Semantic;

            // No effective change (docs/40): the form submits every INITIALIZED extension change, so an
            // untouched one arrives with Original == Value — same as a main-entity change field. It
            // contributes no patch and takes no concurrency check, so extension and main fields share
            // one concurrency model. FIRST branch, before validation: an untouched persisted value is
            // already valid and need not be re-checked.
            if (TamMerge.SemanticallyEqual(semantic, original, submitted)) continue;

            if (submitted is not null)
            {
                if (semantic.Validate(semantic.Normalize(submitted)) is { } invalid)
                {
                    findings.Add(invalid.At(FieldPath.Extension(key)));
                    continue;
                }
                if (spec.Options is { Count: > 0 } options && submitted is string s && !options.Contains(s))
                {
                    findings.Add(ExtensionFindings.InvalidOption.At(FieldPath.Extension(key)));
                    continue;
                }
            }

            if (TamMerge.SemanticallyEqual(semantic, current, submitted)) continue;

            if (!entityIsNew && !TamMerge.SemanticallyEqual(semantic, current, original))
            {
                conflicts.Add(new FieldConflict(
                    FieldPath.Extension(key).Value, original, current, submitted));
                continue;
            }

            entity.Extensions = entity.Extensions.WithValue(key, semantic.Normalize(submitted));
            applied.Add(key);
        }

        if (entityIsNew)
        {
            foreach (var spec in specs.Where(s => s is
                     {
                         Required: true, State: ExtensionFieldState.Active,
                     }))
            {
                if (entity.Extensions.Raw(spec.Key) is null)
                    findings.Add(ValidationFindings.Required.At(FieldPath.Extension(spec.Key)));
            }
        }

        return new ExtensionApplyResult(findings, conflicts, applied);
    }

    /// <summary>The subset of submitted extension changes that are a REAL patch — Original != Value
    /// (Sol re-review round 10, F2). Complete-state submission sends every INITIALIZED extension field,
    /// so an edit form carries unchanged ones (Original == Value); filtering them here lets the pipeline
    /// skip extension-target selection and application entirely when nothing actually changed, so an
    /// operation with no extension change never trips ambiguous-extension-target merely because complete
    /// state carried unchanged values. A known field compares by its semantic; an unknown key is a no-op
    /// only if its raw Original == Value (otherwise retained, so <see cref="Apply"/> reports
    /// unknown-field). An unchanged retired/read-only field is likewise dropped as a no-op — matching the
    /// compiled-field rule that unchanged historical values do not block an unrelated edit.</summary>
    public static IReadOnlyDictionary<string, ExtensionChange> EffectivePatch(
        IReadOnlyDictionary<string, ExtensionChange> changes, IReadOnlyList<ExtensionFieldSpec> specs)
    {
        var byKey = specs.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var effective = new Dictionary<string, ExtensionChange>(StringComparer.Ordinal);
        foreach (var (key, change) in changes)
        {
            var original = FromElement(change.Original);
            var value = FromElement(change.Value);
            var unchanged = byKey.TryGetValue(key, out var spec)
                ? TamMerge.SemanticallyEqual(spec.Semantic, original, value)
                : Equals(original, value);
            if (!unchanged) effective[key] = change;
        }
        return effective;
    }

    private static object? FromElement(JsonElement? element) =>
        element is { } e ? ExtensionData.FromElement(e) : null;
}
