namespace Tam;

public sealed record Option(object Value, string Label);

/// <summary>
/// Output of a server derivation: field-state deltas + findings. Immutable, combinable
/// (docs/05). Suggested values ride as wire-ready values.
/// </summary>
public sealed record DerivationResult(
    IReadOnlyList<Finding> Findings,
    IReadOnlyDictionary<string, IReadOnlyList<Option>> Options,
    IReadOnlyDictionary<string, object?> Suggestions)
{
    public static readonly DerivationResult Empty = new(
        [], new Dictionary<string, IReadOnlyList<Option>>(), new Dictionary<string, object?>());

    public static DerivationResult FieldError(string member, FindingFactory factory) =>
        Empty.AddFieldError(member, factory);

    public static DerivationResult From(Result ruleResult, string target) => Empty with
    {
        Findings = ruleResult.Findings.Select(f => f.Targets.Count == 0 ? f.At(target) : f).ToList(),
    };

    public static DerivationResult Options_(string member, IReadOnlyList<Option> options) =>
        Empty.AddOptions(member, options);

    public DerivationResult AddFieldError(string member, FindingFactory factory) => this with
    {
        Findings = [.. Findings, factory.At(member)],
    };

    public DerivationResult AddWarning(FindingFactory factory) => this with
    {
        Findings = [.. Findings, factory.Create()],
    };

    public DerivationResult Add(Finding finding) => this with { Findings = [.. Findings, finding] };

    public DerivationResult AddOptions(string member, IReadOnlyList<Option> options) => this with
    {
        Options = new Dictionary<string, IReadOnlyList<Option>>(Options)
        {
            [Naming.Camel(member)] = options,
        },
    };

    public DerivationResult Suggest(string member, object? value) => this with
    {
        Suggestions = new Dictionary<string, object?>(Suggestions) { [Naming.Camel(member)] = value },
    };

    public DerivationResult Merge(DerivationResult other) => new(
        [.. Findings, .. other.Findings],
        Options.Concat(other.Options).ToDictionary(kv => kv.Key, kv => kv.Value),
        Suggestions.Concat(other.Suggestions).ToDictionary(kv => kv.Key, kv => kv.Value));
}

/// <summary>Wire shape of one field's fully resolved interaction state.</summary>
public sealed record ResolvedFieldState(
    bool Visible,
    bool Enabled,
    bool Required,
    object? SuggestedValue,
    IReadOnlyList<Option>? Options,
    IReadOnlyList<Finding> Findings);

public sealed record ResolveResponse(
    IReadOnlyDictionary<string, ResolvedFieldState> Fields,
    IReadOnlyList<Finding> Findings,
    long Revision);
