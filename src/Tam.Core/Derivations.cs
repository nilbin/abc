namespace Tam;

public sealed record Option(object Value, string Label);

/// <summary>One AUTHORITATIVE conditional-requiredness rule a derivation produced (docs/40):
/// when <see cref="When"/> holds, <see cref="Field"/> is required. Resolve shows the indicator;
/// submit BLOCKS with <see cref="Finding"/> if the field is empty, for every caller.</summary>
public sealed record RequiredRule(string Field, bool When, Finding Finding);

/// <summary>An AUTHORITATIVE lookup binding a derivation produced (docs/40): <see cref="Field"/>'s
/// submitted value must EXIST in the view <see cref="ViewId"/> constrained by <see cref="Filters"/>
/// — the candidate universe. Submit validates membership by an Exists against that base query (never
/// the front end's browsed page); a value outside it is rejected with <see cref="Invalid"/>.</summary>
public sealed record LookupBinding(
    string Field, string ViewId, IReadOnlyDictionary<string, string?> Filters, Finding Invalid);

/// <summary>An AUTHORITATIVE closed inline option set (docs/40): the SMALL-set twin of a lookup
/// binding. <see cref="Field"/>'s submitted value must be one of <see cref="Options"/> — the complete
/// legal set, not a recommendation. Submit rejects a value outside it with <see cref="Invalid"/>.
/// Use for a handful of context-computed admissible values; a large candidate set is a lookup View.</summary>
public sealed record ClosedOptionSet(string Field, IReadOnlyList<Option> Options, Finding Invalid);

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

    /// <summary>Authoritative lookup-membership bindings (docs/40) — see <see cref="Lookup"/>.</summary>
    public IReadOnlyList<LookupBinding> Lookups { get; init; } = [];

    /// <summary>Authoritative closed inline option sets (docs/40) — see <see cref="Options"/>.</summary>
    public IReadOnlyList<ClosedOptionSet> ClosedOptions { get; init; } = [];

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

    /// <summary>ADVISORY options (docs/40): offered as the candidate set at resolve for display and
    /// ordering, but NOT enforced at submit — a value outside them still passes. For an authoritative
    /// closed set use <see cref="Options"/>; for a large candidate universe use <see cref="Lookup"/>.</summary>
    public DerivationResult AddOptions(string member, IReadOnlyList<Option> options) => this with
    {
        Options = new Dictionary<string, IReadOnlyList<Option>>(Options)
        {
            [Naming.Camel(member)] = options,
        },
    };

    /// <summary>AUTHORITATIVE closed inline options (docs/40): <paramref name="options"/> are BOTH
    /// offered at resolve AND the complete legal set at submit — a submitted value outside them is
    /// rejected with <paramref name="invalid"/>, for every caller. The small-set twin of <see
    /// cref="Lookup"/>; advisory recommendations use <see cref="AddOptions"/> instead.</summary>
    public DerivationResult RequireOneOf(string member, IReadOnlyList<Option> options, FindingFactory invalid) =>
        AddOptions(member, options) with
        {
            ClosedOptions = [.. ClosedOptions,
                new ClosedOptionSet(Naming.Camel(member), options, invalid.At(Naming.Camel(member)))],
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

    /// <summary>AUTHORITATIVE lookup membership (docs/40): the submitted value of <paramref
    /// name="member"/> must EXIST in view <paramref name="viewId"/> constrained by <paramref
    /// name="filters"/> (the candidate universe — e.g. open projects of the picked customer). Submit
    /// validates by an Exists against those base filters, ignoring the client's browsing params, and
    /// rejects a value outside the universe with <paramref name="invalid"/>. The base filters are the
    /// view's own Filterable fields — one mechanism for browsing and the authoritative constraint.</summary>
    public DerivationResult Lookup(
        string member, string viewId, IReadOnlyDictionary<string, string?> filters, FindingFactory invalid) => this with
    {
        Lookups = [.. Lookups,
            new LookupBinding(Naming.Camel(member), viewId, filters, invalid.At(Naming.Camel(member)))],
    };

    public DerivationResult Merge(DerivationResult other) => new(
        [.. Findings, .. other.Findings],
        Options.Concat(other.Options).ToDictionary(kv => kv.Key, kv => kv.Value),
        Suggestions.Concat(other.Suggestions).ToDictionary(kv => kv.Key, kv => kv.Value))
    {
        Required = [.. Required, .. other.Required],
        Lookups = [.. Lookups, .. other.Lookups],
        ClosedOptions = [.. ClosedOptions, .. other.ClosedOptions],
    };
}

/// <summary>The runtime lookup binding a resolved field carries (docs/40, Sol re-review Finding 6):
/// the candidate View plus the contextual base filters the derivation computed (e.g. the picked
/// customer). The client opens the View scoped by these filters — so it browses (paginates, searches,
/// sorts) exactly the authoritative candidate universe submit validates against, instead of the
/// derivation materializing the whole set as inline options.</summary>
public sealed record ResolvedLookup(string ViewId, IReadOnlyDictionary<string, string?> BaseFilters);

/// <summary>Wire shape of one field's fully resolved interaction state.</summary>
public sealed record ResolvedFieldState(
    bool Visible,
    bool Enabled,
    bool Required,
    object? SuggestedValue,
    IReadOnlyList<Option>? Options,
    ResolvedLookup? Lookup,
    IReadOnlyList<Finding> Findings);

public sealed record ResolveResponse(
    IReadOnlyDictionary<string, ResolvedFieldState> Fields,
    IReadOnlyList<Finding> Findings,
    long Revision);
