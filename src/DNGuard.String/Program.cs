using System.Text.Json;
using DNGuard.Common;

namespace DNGuard.String;

internal static class EntryPoint
{
    private static int Main(string[] args)
    {
        var cli = new Cli(args);
        if (cli.Has("--help") || cli.Has("-h")) { Usage(); return 0; }

        var target = cli.Value("--target") ?? cli.FirstFile();
        var dragDrop = args.Length > 0 && !args[0].StartsWith('-') && File.Exists(args[0]);
        if (string.IsNullOrWhiteSpace(target))
        {
            Usage();
            PauseIfNeeded(dragDrop, cli);
            return 2;
        }

        try
        {
            var ws = WorkspaceLayout.FromTarget(target);
            ws.EnsureAll();

            var rebuilt = cli.Value("--rebuilt") ?? ws.DetectLatestRebuilt();
            if (string.IsNullOrWhiteSpace(rebuilt) || !File.Exists(rebuilt))
            {
                ConsoleEx.Error("Rebuilt DLL not found.");
                ConsoleEx.Dim("Run DNGuard.Rebuild.exe first or pass --rebuilt <dll>.");
                PauseIfNeeded(dragDrop, cli);
                return 3;
            }

            var host = Path.GetFullPath(cli.Value("--host") ?? ws.TargetPath);
            var hook = ToolLocator.Find(ws, "StrDumpHook.dll", cli.Value("--hook"));
            if (hook is null)
            {
                ConsoleEx.Error("StrDumpHook.dll not found.");
                ConsoleEx.Dim("Place the hook next to DNGuard.String.exe, in payloads\\, workspace tools\\, or pass --hook.");
                PauseIfNeeded(dragDrop, cli);
                return 3;
            }

            var output = Path.GetFullPath(cli.Value("--out") ?? ws.DefaultStringsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var logPath = Path.Combine(ws.LogsDirectory, $"strings-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var tee = new ColorTeeWriter(Console.Out, logPath);
            Console.SetOut(tee);
            Console.SetError(tee);

            ConsoleEx.Banner("DNGuard String-Decryptor 1.0.2 — runtime string dump + ldstr inliner");
            ConsoleEx.Info("Rebuilt : " + rebuilt);
            ConsoleEx.Info("Host    : " + host);

            var rc = RunStringPipeline(
                rebuilt,
                host,
                hook,
                output,
                cli.Int("--timeout", 180),
                !cli.Has("--quiet"));

            if (rc == 0)
            {
                var map = Path.Combine(ws.StringsDirectory, "strings.json");
                if (File.Exists(map))
                {
                    var count = CountStrings(map);
                    ConsoleEx.Ok($"Inlined   : {count:N0} runtime strings");
                }

                File.WriteAllText(Path.Combine(ws.StringsDirectory, "latest.txt"), output);
                File.WriteAllText(Path.Combine(ws.StringsDirectory, "string-session.json"),
                    JsonSerializer.Serialize(new
                    {
                        target = ws.TargetPath,
                        host,
                        rebuilt = Path.GetFullPath(rebuilt),
                        hook,
                        output,
                        strings = map,
                        completedAt = DateTimeOffset.Now
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                ConsoleEx.Error("String stage failed. See log: " + logPath);
                ConsoleEx.Dim("If the app needs login or loads classes later, increase --timeout or launch the correct host capable of loading the module.");
            }

            PauseIfNeeded(dragDrop, cli);
            return rc;
        }
        catch (Exception ex)
        {
            ConsoleEx.Error(ex.ToString());
            PauseIfNeeded(dragDrop, cli);
            return 1;
        }
    }

    private static int RunStringPipeline(
        string rebuiltModule,
        string host,
        string hook,
        string output,
        int timeoutSeconds,
        bool verbose)
    {
        // Call the Engine's string stage directly (scan accessors → runtime dump → inline ldstr)
        // instead of the full 5-phase rebuild pipeline.
        return global::Program.RunStringDecryptFromApi(
            rebuiltModule, host, hook, output, verbose, timeoutSeconds);
    }

    private static int CountStrings(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().Count()
                : 0;
        }
        catch { return 0; }
    }

    private static void PauseIfNeeded(bool dragDrop, Cli cli)
    {
        if (cli.Has("--no-pause") || Console.IsInputRedirected) return;
        Console.WriteLine();
        ConsoleEx.Dim("Press any key to exit...");
        try { Console.ReadKey(intercept: true); } catch { }
    }

    private static void Usage()
    {
        Console.WriteLine("""
DNGuard.String <target.exe>
  [--rebuilt <rebuilt.dll>] [--host <protected-host.exe>]
  [--hook <StrDumpHook.dll>] [--out <strings.dll>]
  [--timeout <seconds>] [--quiet] [--no-pause]

You can drag the target EXE onto DNGuard.String.exe.
The tool automatically finds the latest rebuilt DLL and StrDumpHook.dll, then writes:
  _dnguard\\<target>\\strings\\<target>.rebuilt.strings.dll
  _dnguard\\<target>\\strings\\strings.json

StrDumpHook.dll can be placed next to the tool, in payloads\\, workspace tools\\,
DNGUARD_TOOLS, C:\\tool_dng, or C:\\Tools.
""");
    }
}
