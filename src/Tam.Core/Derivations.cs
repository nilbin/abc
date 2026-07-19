namespace Tam;

public sealed record Option(object Value, string Label);

/// <summary>One AUTHORITATIVE conditional-requiredness rule a derivation produced (docs/40):
/// when <see cref="When"/> holds, <see cref="Field"/> is required. Resolve shows the indicator;
/// submit BLOCKS with <see cref="Finding"/> if the field is empty, for every caller.</summary>
public sealed record RequiredRule(string Field, bool When, Finding Finding);

/// <summary>
/// Output of a server derivation (docs/05, docs/40): field-state deltas + findings. Immutable,
/// combinable. Outputs carry their OWN authority: <see cref="Required"/> and blocking findings are
/// authoritative (enforced at submit for every caller); <see cref="Suggestions"/>, <see
/// cref="Options"/> ordering and non-blocking warnings are advisory (consumed by resolve, ignored
/// at submit). Authority is a property of the output, never of the whole method.
/// </summary>
public sealed record DerivationResult(
    IReadOnlyList<Finding> Findings,
    IReadOnlyDictionary<string, IReadOnlyList<Option>> Options,
    IReadOnlyDictionary<string, object?> Suggestions)
{
    /// <summary>Authoritative conditional requiredness (docs/40) — see <see cref="Require"/>.</summary>
    public IReadOnlyList<RequiredRule> Required { get; init; } = [];

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

    /// <summary>AUTHORITATIVE conditional requiredness (docs/40): when <paramref name="when"/> holds,
    /// <paramref name="member"/> is required — resolve shows the indicator, and submit BLOCKS with
    /// <paramref name="finding"/> if it is empty, for EVERY caller (direct, MCP, integration), not
    /// merely the form the field is drawn on. Unlike a suggestion or a warning, this output is
    /// enforced at submit. The domain-specific finding is preserved (e.g. orders.project-required),
    /// so callers get the precise message, not a generic validation.required.</summary>
    public DerivationResult Require(string member, bool when, FindingFactory finding) => this with
    {
        Required = [.. Required, new RequiredRule(Naming.Camel(member), when, finding.At(Naming.Camel(member)))],
    };

    public DerivationResult Merge(DerivationResult other) => new(
        [.. Findings, .. other.Findings],
        Options.Concat(other.Options).ToDictionary(kv => kv.Key, kv => kv.Value),
        Suggestions.Concat(other.Suggestions).ToDictionary(kv => kv.Key, kv => kv.Value))
    {
        Required = [.. Required, .. other.Required],
    };
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
