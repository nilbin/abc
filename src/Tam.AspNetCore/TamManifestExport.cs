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
        if (args is ["impact", ..])
        {
            // Step 12: the consolidated change-impact report — the compiled model vs the
            // committed baseline. Exit 2 on D4-breaking changes so scripts can branch on it;
            // the CI gate proper stays scripts/check_manifest.py.
            var baselinePath = args.Length > 1 ? args[1] : "manifest.baseline.json";
            var baseline = JsonSerializer.Deserialize<ManifestDto>(
                File.ReadAllText(baselinePath), TamJson.Options)!;
            var report = TamImpact.Against(model, baseline);
            Console.WriteLine($"impact vs {baselinePath}:");
            Console.WriteLine(report.Format());
            if (report.HasBreaks) Environment.ExitCode = 2;
            return true;
        }

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
