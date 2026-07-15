using System.Text.Json;

namespace Tam;

/// <summary>
/// Per-culture message/label catalogs (docs/21-localization.md). No display text exists in code;
/// every key referenced by the model must exist in the default culture (L10N001, verified at startup).
/// </summary>
public sealed class LocaleCatalogs(string defaultCulture)
{
    private readonly Dictionary<string, Dictionary<string, string>> cultures = new(StringComparer.OrdinalIgnoreCase);

    public string DefaultCulture { get; } = defaultCulture;

    public IReadOnlyCollection<string> Cultures => cultures.Keys;

    public void Add(string culture, IReadOnlyDictionary<string, string> entries)
    {
        if (!cultures.TryGetValue(culture, out var existing))
            cultures[culture] = existing = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, text) in entries)
            existing[key] = text;
    }

    public void AddFromDirectory(string localesDirectory)
    {
        if (!Directory.Exists(localesDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(localesDirectory, "*.json"))
        {
            var culture = Path.GetFileNameWithoutExtension(file);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(file)) ?? [];
            Add(culture, entries);
        }
    }

    public string? Lookup(string key, string culture)
    {
        if (cultures.TryGetValue(culture, out var c) && c.TryGetValue(key, out var text)) return text;
        var neutral = culture.Split('-')[0];
        if (neutral != culture && cultures.TryGetValue(neutral, out var n) && n.TryGetValue(key, out text)) return text;
        if (cultures.TryGetValue(DefaultCulture, out var d) && d.TryGetValue(key, out text)) return text;
        return null;
    }

    /// <summary>Resolves a finding message: code is the key, args fill {placeholders}. Falls back to the code itself.</summary>
    public string ResolveMessage(string code, IReadOnlyDictionary<string, object?> args, string culture)
    {
        var template = Lookup(code, culture) ?? code;
        return Format(template, args, culture);
    }

    /// <summary>
    /// General-purpose lookup + format for code that COMPOSES text (emails, exports): the key
    /// resolves through the culture chain, {placeholders} fill from <paramref name="args"/>, and
    /// a missing key falls back to the key itself. The one replacement for every hand-rolled
    /// "Lookup ?? key then Format" helper.
    /// </summary>
    public string Localize(string key, string culture, IReadOnlyDictionary<string, object?>? args = null) =>
        Format(Lookup(key, culture) ?? key, args ?? Empty, culture);

    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>();

    public Finding Resolve(Finding finding, string culture) =>
        finding.Message is not null ? finding : finding with
        {
            Message = ResolveMessage(finding.Code, finding.Args, culture),
        };

    public IReadOnlyDictionary<string, string> Catalog(string culture) =>
        cultures.TryGetValue(culture, out var c) ? c : new Dictionary<string, string>();

    /// <summary>L10N001/L10N002: default-culture gaps are errors; other cultures produce a report.</summary>
    public IReadOnlyList<string> MissingKeys(IEnumerable<string> requiredKeys, string culture) =>
        requiredKeys.Where(k => Lookup(k, culture) is null).Distinct().Order().ToList();

    public static string Format(string template, IReadOnlyDictionary<string, object?> args, string culture)
    {
        if (args.Count == 0 || !template.Contains('{')) return template;
        var info = System.Globalization.CultureInfo.GetCultureInfo(culture);
        foreach (var (key, value) in args)
        {
            template = template.Replace(
                "{" + key + "}",
                value is IFormattable f ? f.ToString(null, info) : value?.ToString() ?? string.Empty,
                StringComparison.Ordinal);
        }
        return template;
    }
}
