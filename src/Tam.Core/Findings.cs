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

public static class Naming
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
}
