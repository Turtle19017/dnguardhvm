using DNGuard.Common;

namespace DNGuard.Index;

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

            var corpus = cli.Value("--corpus");
            if (corpus is null && ConsoleEx.Interactive && !cli.Has("--auto"))
                corpus = PickCorpus(ws);
            corpus ??= ws.DetectCurrentCapture();

            if (corpus is null || !Directory.Exists(Path.Combine(corpus, "methods")))
            {
                ConsoleEx.Error("Current corpus not found.");
                ConsoleEx.Dim("Run DNGuard.Capture.exe first or pass --corpus <folder>.");
                ConsoleEx.PauseOnExit();
                return 3;
            }

            var output = Path.GetFullPath(cli.Value("--out") ?? ws.IndexDirectory);
            var targetNamespace = cli.Value("--namespace") ?? ws.TargetName;
            var logPath = Path.Combine(ws.LogsDirectory, $"index-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var tee = new ColorTeeWriter(Console.Out, logPath);
            Console.SetOut(tee);
            Console.SetError(tee);

            ConsoleEx.Banner("DNGuard Corpus-Merger 1.0 — IL corpus consolidation & tokenmap builder");
            ConsoleEx.Info("Corpus→Index  : " + Path.GetFileName(corpus) + " → " + targetNamespace);

            var rc = global::Program.BuildIndex(
                new[] { Path.GetFullPath(corpus) },
                output,
                targetNamespace,
                clean: !cli.Has("--no-clean"),
                verbose: !cli.Has("--quiet"));

            if (rc == 0)
                ConsoleEx.Dim("Next step: drag the target onto DNGuard.Rebuild.exe");
            ConsoleEx.PauseOnExit();
            return rc;
        }
        catch (Exception ex)
        {
            ConsoleEx.Error(ex.ToString());
            ConsoleEx.PauseOnExit();
            return 1;
        }
    }

    private static string? PickCorpus(WorkspaceLayout ws)
    {
        var sessionsRoot = ws.CaptureSessions;
        if (!Directory.Exists(sessionsRoot)) return null;
        var sessions = Directory.EnumerateDirectories(sessionsRoot)
            .Where(d => Directory.Exists(Path.Combine(d, "methods")))
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Take(9)
            .ToList();
        if (sessions.Count == 0) return null;

        Console.WriteLine();
        ConsoleEx.Warn("Select a capture session to merge (Enter = latest, 0 = skip):");
        for (var i = 0; i < sessions.Count; i++)
        {
            var methodDirs = Directory.EnumerateDirectories(Path.Combine(sessions[i].FullName, "methods")).Count();
            Console.WriteLine($"    [{i + 1}] {sessions[i].Name}  ({methodDirs:N0} method dirs, {sessions[i].LastWriteTime:yyyy-MM-dd HH:mm:ss})");
        }
        Console.Write("    > ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return sessions[0].FullName;
        if (input == "0") return null;
        return int.TryParse(input, out var n) && n >= 1 && n <= sessions.Count
            ? sessions[n - 1].FullName
            : sessions[0].FullName;
    }

    private static void Usage()
    {
        Console.WriteLine("""
DNGuard.Index <target.exe>
  [--corpus <capture-session>] [--out <index-dir>] [--namespace <root-ns>]
  [--auto] [--no-clean] [--quiet]

You can drag the target EXE onto DNGuard.Index.exe.
An interactive session picker is shown when no --corpus is given
(--auto skips the picker and uses the latest capture).
Default output: <target-dir>\_dnguard\<target>\index\
""");
    }
}
