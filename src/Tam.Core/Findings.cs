namespace Tam;

public enum FindingSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>Wire path of a field: camelCase input member ("workAddress") or extension key ("extensions.machineSerialNumber").</summary>
public readonly record struct FieldPath(string Value)
{
    public override string ToString() => Value;

    public static FieldPath For(string memberName) => new(Naming.Camel(memberName));

    public static FieldPath Extension(string key) => new($"extensions.{key}");
}

/// <summary>
/// The universal feedback shape. Message text is never authored in code: <see cref="Code"/> is the
/// message key, <see cref="Args"/> are the template parameters, and <see cref="Message"/> is resolved
/// at the boundary in the request culture (docs/21-localization.md).
/// </summary>
public sealed record Finding(
    string Code,
    FindingSeverity Severity,
    IReadOnlyDictionary<string, object?> Args,
    IReadOnlyList<FieldPath> Targets,
    bool BlocksSubmission,
    string? Message = null)
{
    public static FindingFactory Error(string code) => new(code, FindingSeverity.Error);

    public static FindingFactory Warning(string code) => new(code, FindingSeverity.Warning);

    public static FindingFactory Information(string code) => new(code, FindingSeverity.Information);

    public Finding At(FieldPath path) => this with { Targets = [.. Targets, path] };

    public Finding At(string memberName) => At(FieldPath.For(memberName));
}

/// <summary>A declared finding: stable code + severity. Text lives in locale catalogs under the code as key.</summary>
public sealed record FindingFactory(string Code, FindingSeverity Severity)
{
    private static readonly IReadOnlyDictionary<string, object?> NoArgs =
        new Dictionary<string, object?>();

    public Finding Create() => new(
        Code, Severity, NoArgs, [], BlocksSubmission: Severity == FindingSeverity.Error);

    public Finding With(params (string Key, object? Value)[] args) => Create() with
    {
        Args = args.ToDictionary(a => a.Key, a => a.Value),
    };

    public Finding At(string memberName) => Create().At(memberName);

    public Finding At(FieldPath path) => Create().At(path);

    public static implicit operator Finding(FindingFactory factory) => factory.Create();
}

public static class PipelineFindings
{
    public static readonly FindingFactory NotAuthorized = Finding.Error("pipeline.not-authorized");
    public static readonly FindingFactory UnknownOperation = Finding.Error("pipeline.unknown-operation");
    public static readonly FindingFactory UnknownView = Finding.Error("pipeline.unknown-view");
    public static readonly FindingFactory UnknownForm = Finding.Error("pipeline.unknown-form");
    public static readonly FindingFactory InvalidInput = Finding.Error("pipeline.invalid-input");
    public static readonly FindingFactory NotFound = Finding.Error("pipeline.not-found");
    public static readonly FindingFactory IdempotentReplay = Finding.Information("pipeline.idempotent-replay");
    public static readonly FindingFactory FieldNotAuthorized = Finding.Error("pipeline.field-not-authorized");
    public static readonly FindingFactory AmbiguousExtensionTarget = Finding.Error("pipeline.ambiguous-extension-target");
    public static readonly FindingFactory ExtensionTargetNotFound = Finding.Error("pipeline.extension-target-not-found");
    public static readonly FindingFactory ReplayActorUnavailable = Finding.Error("pipeline.replay-actor-unavailable");
}

public static partial class Naming
{
    public static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public static string Kebab(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The wire-safe SLUG shape — tenant-authored names that become ids or derived
    /// codes (rule names, role names, tenant ids): lowercase words joined by hyphens. One
    /// definition for the one shape (beauty arc 1); surfaces name their own finding.</summary>
    public static bool IsSlug(string value) => SlugRegex().IsMatch(value);

    /// <summary>The camelCase KEY shape — extension field keys and their package twins:
    /// a lowercase first word, camel humps after, no separators.</summary>
    public static bool IsCamelKey(string value) => CamelKeyRegex().IsMatch(value);

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial System.Text.RegularExpressions.Regex SlugRegex();

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-zA-Z0-9]*$")]
    private static partial System.Text.RegularExpressions.Regex CamelKeyRegex();
}

/// <summary>
/// The label-key grammar, minted in ONE place: every convention the catalogs, clients and
/// L10N001 rely on has a named factory here — never a string interpolation at a call site
/// (docs/21). A new kind of labeled thing earns a new method, not a new inline pattern.
/// </summary>
public static class LabelKeys
{
    /// <summary>Default field label: "labels.{kebab(member)}".</summary>
    public static string Field(string memberName) => $"labels.{Naming.Kebab(memberName)}";

    public static string OperationTitle(string operationId) => $"operations.{operationId}.title";

    public static string IntegrationTitle(string integrationId) => $"integrations.{integrationId}.title";

    public static string Nav(string nodeId) => $"nav.{nodeId}";

    /// <summary>Tenant extension field label — resolved from the spec's own Labels, merged
    /// into the catalogs per tenant (docs/15).</summary>
    public static string Extension(string key) => $"ext.{key}";

    public static string ExtensionDescription(string key) => $"ext.{key}.description";
}
