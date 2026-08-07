using System.Text.Json;
using DNGuard.Common;

namespace DNGuard.Status;

internal static class EntryPoint
{
    private static int Main(string[] args)
    {
        var cli = new Cli(args);
        if (cli.Has("--help") || cli.Has("-h")) { Usage(); return 0; }
        var target = cli.Value("--target") ?? cli.FirstFile();
        if (string.IsNullOrWhiteSpace(target)) { Usage(); return 2; }

        try
        {
            var ws = WorkspaceLayout.FromTarget(target);
            ws.EnsureAll();
            var capture = ws.DetectCurrentCapture();
            var captureMethods = capture is not null && Directory.Exists(Path.Combine(capture, "methods"))
                ? Directory.EnumerateDirectories(Path.Combine(capture, "methods")).Count() : 0;
            var dump = ws.DetectDump(cli.Value("--dump"));
            var indexStatsPath = Path.Combine(ws.IndexDirectory, "stats.json");
            var latest = ws.DetectLatestRebuilt();
            var validationPath = latest is null ? null : Path.Combine(Path.GetDirectoryName(latest)!, "semantic-validation.json");

            object? indexStats = ReadJson(indexStatsPath);
            object? validation = validationPath is not null ? ReadJson(validationPath) : null;
            var report = new
            {
                target = ws.TargetPath,
                workspace = ws.Root,
                dump,
                capture,
                captureMethodDirectories = captureMethods,
                indexDirectory = Directory.Exists(Path.Combine(ws.IndexDirectory, "by-token")) ? ws.IndexDirectory : null,
                indexStats,
                latestRebuilt = latest,
                validation
            };

            if (cli.Has("--json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            ConsoleEx.Banner("DNGuard Status 1.0 — workspace overview");
            ConsoleEx.Info("Target    : " + ws.TargetPath);
            ConsoleEx.Info("Workspace : " + ws.Root);
            PrintPath("Dump", dump);
            PrintPath("Capture", capture);
            ConsoleEx.Dim($"Capture methods: {captureMethods:N0}");
            PrintPath("Index", Directory.Exists(Path.Combine(ws.IndexDirectory, "by-token")) ? ws.IndexDirectory : null);
            if (indexStats is JsonElement i) PrintIndex(i);
            PrintPath("Rebuilt", latest);
            if (validation is JsonElement v) PrintValidation(v);
            return 0;
        }
        catch (Exception ex) { ConsoleEx.Error(ex.ToString()); return 1; }
    }

    private static JsonElement? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static void PrintPath(string label, string? path)
    {
        if (path is null) ConsoleEx.Warn($"{label,-9}: not found");
        else ConsoleEx.Ok($"{label,-9}: {path}");
    }

    private static void PrintIndex(JsonElement root)
    {
        ConsoleEx.Dim($"Index methods={N(root, "targetMethods")} tokenmap entries={N(root, "tokenMapEntries")} inconsistent={N(root, "inconsistentMethods")}");
    }

    private static void PrintValidation(JsonElement root)
    {
        ConsoleEx.Dim($"Validation methods={N(root, "Methods")} structural={N(root, "StructurallyValid")} core={N(root, "SemanticCoreClean")} strict={N(root, "SemanticStrictClean")}");
        ConsoleEx.Dim($"Residual invalid={N(root, "InvalidMetadataOperands")} stubs={N(root, "DnGuardStubs")} object={N(root, "ObjectFallbackOperands")}");
    }

    private static long N(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.TryGetInt64(out var value) ? value : 0;

    private static void Usage()
    {
        Console.WriteLine("""
DNGuard.Status <target.exe> [--json]

Hiển thị dump, capture hiện tại, index, DLL rebuilt mới nhất và validator.
""");
    }
}
