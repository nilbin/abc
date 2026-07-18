using System.Text.Json;

/// <summary>A tam.json, resolved: the host project, the committed artifact paths, the plugin
/// contract slices, and the TS codegen command — every path absolute, rooted at the tam.json's
/// directory. This is the ONLY layout knowledge in the tool, so a consumer points it at their
/// own repo with their own paths and everything else follows.</summary>
sealed class TamConfig
{
    public required string Root { get; init; }
    public required string Host { get; init; }
    public required string Manifest { get; init; }
    public required string HostContract { get; init; }
    public required string WebTypes { get; init; }
    public required IReadOnlyList<Slice> Slices { get; init; }
    private string[] TypesCommandTemplate { get; init; } = [];

    public string HostRelative => Relative(Host);
    public string Relative(string path) => Path.GetRelativePath(Root, path);

    /// <summary>The codegen command with {manifest}/{webTypes} substituted — {webTypes} can be
    /// overridden (verify writes to a temp file and compares).</summary>
    public (string Exe, string[] Args) TypesCommand(string? webTypesOverride)
    {
        if (TypesCommandTemplate.Length == 0)
            throw new TamCliException("tam.json has no \"typesCommand\" — cannot generate the TS client.");
        var outPath = webTypesOverride ?? WebTypes;
        var resolved = TypesCommandTemplate
            .Select(part => part
                .Replace("{manifest}", Manifest)
                .Replace("{webTypes}", outPath))
            .ToArray();
        return (resolved[0], resolved[1..]);
    }

    public static TamConfig Read(string path, string root)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        string Abs(string p) => Path.GetFullPath(Path.Combine(root, p));
        string Req(string key) => r.TryGetProperty(key, out var v) && v.GetString() is { } s
            ? Abs(s)
            : throw new TamCliException($"tam.json is missing required \"{key}\".");

        var slices = new List<Slice>();
        if (r.TryGetProperty("slices", out var declared) && declared.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in declared.EnumerateArray())
                slices.Add(new Slice(
                    s.GetProperty("pluginId").GetString()!,
                    Abs(s.GetProperty("path").GetString()!)));
        }
        else
        {
            // Auto-discover: every committed *.contract.json (dot suffix keeps host-contract.json
            // out), pluginId derived from the filename. A new dependency parent needs no edit.
            foreach (var file in DiscoverSlices(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                slices.Add(new Slice(name[..^".contract.json".Length], file));
            }
        }

        var typesCommand = r.TryGetProperty("typesCommand", out var tc) && tc.ValueKind == JsonValueKind.Array
            ? tc.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];

        return new TamConfig
        {
            Root = root,
            Host = Req("host"),
            Manifest = Req("manifest"),
            HostContract = Req("hostContract"),
            WebTypes = Req("webTypes"),
            Slices = slices,
            TypesCommandTemplate = typesCommand,
        };
    }

    private static IEnumerable<string> DiscoverSlices(string root)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal) { "bin", "obj", "node_modules", ".git" };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!skip.Contains(Path.GetFileName(sub)))
                    stack.Push(sub);
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                if (Path.GetFileName(file).EndsWith(".contract.json", StringComparison.Ordinal))
                    yield return file;
        }
    }
}

sealed record Slice(string PluginId, string Path);

sealed class TamCliException(string message) : Exception(message);
