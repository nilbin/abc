using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

// `dotnet tam` — the framework's artifact-regeneration surface for ANY Tam host. It orchestrates
// the exports the host already exposes (`dotnet run -- manifest|contract`, which need the COMPOSED
// runtime model, so they cannot be a source generator) plus TS codegen, driven by a `tam.json`
// that names the layout — zero hardcoded paths, so a consumer runs it in their own repo.
//
//   dotnet tam regen     regenerate every committed artifact, then `git add -A`
//   dotnet tam verify    re-export to a temp dir and byte-compare — the CI freshness gate
//   dotnet tam manifest [path]                 one-off manifest export
//   dotnet tam contract [path] [--plugin id]   one-off host / plugin-slice export

var command = args.Length > 0 ? args[0] : "help";
try
{
    return command switch
    {
        "regen" => Regen(),
        "verify" => Verify(),
        "manifest" => PassThrough("manifest", args[1..]),
        "contract" => PassThrough("contract", args[1..]),
        "help" or "--help" or "-h" => Help(),
        _ => Fail($"unknown command '{command}'. Try `dotnet tam help`."),
    };
}
catch (TamCliException ex)
{
    Console.Error.WriteLine($"tam: {ex.Message}");
    return 1;
}

int Help()
{
    Console.WriteLine("""
        dotnet tam — regenerate and verify a Tam host's committed model artifacts.

          regen                          rebuild the host and rewrite manifest, host contract,
                                         every plugin contract slice, and the TS client
          verify                         re-export to a temp dir and byte-compare (CI freshness)
          manifest [path]                export just the manifest
          contract [path] [--plugin id]  export the host contract, or one plugin's slice

        Layout comes from tam.json (found by walking up from the working directory).
        """);
    return 0;
}

// --- config ---------------------------------------------------------------------------------

TamConfig LoadConfig()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    for (; dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "tam.json");
        if (File.Exists(candidate))
            return TamConfig.Read(candidate, dir.FullName);
    }
    throw new TamCliException("no tam.json found in this directory or any parent. See docs/38-tooling.md.");
}

// --- commands -------------------------------------------------------------------------------

int Regen()
{
    var config = LoadConfig();
    Console.WriteLine($"host: {config.HostRelative}");
    RunDotnet(config.Root, "build", config.Host, "-v", "q", "--nologo");

    Export(config, "manifest", config.Manifest);
    Export(config, "contract", config.HostContract);
    foreach (var slice in config.Slices)
    {
        Export(config, "contract", slice.Path, "--plugin", slice.PluginId);
        Console.WriteLine($"  slice: {slice.PluginId}");
    }
    GenerateTypes(config);

    Console.WriteLine("regenerated: manifest, host contract, plugin slices, TS client — now: git add -A");
    return 0;
}

int Verify()
{
    var config = LoadConfig();
    RunDotnet(config.Root, "build", config.Host, "-v", "q", "--nologo");

    var temp = Directory.CreateTempSubdirectory("tam-verify-").FullName;
    var stale = new List<string>();

    void CompareJson(string committed, params string[] exportArgs)
    {
        var fresh = Path.Combine(temp, Path.GetFileName(committed) + ".fresh");
        ExportTo(config, fresh, exportArgs);
        if (!JsonEqual(committed, fresh)) stale.Add(config.Relative(committed));
    }

    CompareJson(config.Manifest, "manifest");
    CompareJson(config.HostContract, "contract");
    foreach (var slice in config.Slices)
        CompareJson(slice.Path, "contract", "--plugin", slice.PluginId);

    // The TS client: regenerate to a temp file and compare text.
    var freshTypes = Path.Combine(temp, "tam.ts.fresh");
    GenerateTypes(config, freshTypes);
    if (!TextEqual(config.WebTypes, freshTypes)) stale.Add(config.Relative(config.WebTypes));

    if (stale.Count == 0)
    {
        Console.WriteLine("tam: all artifacts current");
        return 0;
    }
    Console.Error.WriteLine("tam: stale artifacts — run `dotnet tam regen` and commit:");
    foreach (var f in stale) Console.Error.WriteLine($"  {f}");
    return 1;
}

int PassThrough(string mode, string[] rest)
{
    var config = LoadConfig();
    RunDotnet(config.Root, "build", config.Host, "-v", "q", "--nologo");

    // Split rest into the (optional) output path and the flags. "--plugin <id>" is a flag pair;
    // the first bare token is the path, defaulting to the configured artifact.
    string? path = null;
    var flags = new List<string>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--plugin" && i + 1 < rest.Length) { flags.Add(rest[i]); flags.Add(rest[++i]); }
        else if (rest[i].StartsWith("--", StringComparison.Ordinal)) flags.Add(rest[i]);
        else path ??= rest[i];
    }
    var target = path ?? (mode == "manifest" ? config.Manifest : config.HostContract);
    Export(config, mode, target, [.. flags]);
    return 0;
}

// --- exec helpers ---------------------------------------------------------------------------

void Export(TamConfig config, string mode, string outPath, params string[] extra) =>
    ExportTo(config, outPath, [mode, .. extra]);

void ExportTo(TamConfig config, string outPath, string[] modeAndFlags)
{
    // `dotnet run --project <host> -- <mode> <ABSOLUTE outPath> [flags]`. The path MUST be
    // absolute: `dotnet run` uses the project directory as the working directory.
    var mode = modeAndFlags[0];
    var flags = modeAndFlags[1..];
    string[] runArgs = ["run", "--project", config.Host, "--no-build", "--", mode,
        Path.GetFullPath(outPath), .. flags];
    RunDotnet(config.Root, runArgs);
}

void GenerateTypes(TamConfig config, string? overrideOut = null)
{
    var (exe, cmdArgs) = config.TypesCommand(overrideOut);
    var start = new ProcessStartInfo(exe) { WorkingDirectory = config.Root };
    foreach (var a in cmdArgs) start.ArgumentList.Add(a);
    Run(start, $"{exe} {string.Join(' ', cmdArgs)}");
}

void RunDotnet(string cwd, params string[] dotnetArgs)
{
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = cwd };
    foreach (var a in dotnetArgs) start.ArgumentList.Add(a);
    Run(start, $"dotnet {string.Join(' ', dotnetArgs)}");
}

void Run(ProcessStartInfo start, string display)
{
    using var process = Process.Start(start)
        ?? throw new TamCliException($"could not start: {display}");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new TamCliException($"failed (exit {process.ExitCode}): {display}");
}

int Fail(string message) => throw new TamCliException(message);

static bool JsonEqual(string a, string b) =>
    JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(a)), JsonNode.Parse(File.ReadAllText(b)));

static bool TextEqual(string a, string b) => File.ReadAllText(a) == File.ReadAllText(b);
