using System.Text.Json;

namespace Tam.AspNetCore;

public static class TamManifestExport
{
    /// <summary>
    /// The D4 baseline-export CLI mode every host needs: <c>dotnet run -- manifest [path]</c>
    /// writes the compiled model's manifest and returns true (the host exits). No server, no
    /// database — safe in CI. One call at the top of Program.cs instead of copied boilerplate.
    /// </summary>
    public static bool TryHandle(TamModel model, string[] args)
    {
        if (args is not ["manifest", ..]) return false;
        var path = args.Length > 1 ? args[1] : "manifest.baseline.json";
        var exported = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), revision: 0);
        File.WriteAllText(path, JsonSerializer.Serialize(
            exported, new JsonSerializerOptions(TamJson.Options) { WriteIndented = true }));
        Console.WriteLine($"manifest written to {path}");
        return true;
    }
}
