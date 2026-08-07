using DNGuard.Common;

namespace DNGuard.Rebuild;

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
            var dump = ws.DetectDump(cli.Value("--dump"));
            if (dump is null)
            {
                ConsoleEx.Error("Module dump not found.");
                ConsoleEx.Dim($"Place it at {Path.Combine(ws.TargetDirectory, "Dumps", ws.TargetFileName)} or pass --dump.");
                ConsoleEx.PauseOnExit();
                return 3;
            }

            var index = Path.GetFullPath(cli.Value("--index") ?? ws.IndexDirectory);
            var corpus = cli.Value("--corpus") ?? ws.DetectCurrentCapture();
            var output = Path.GetFullPath(cli.Value("--out") ?? ws.DefaultRebuiltPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var logPath = Path.Combine(ws.LogsDirectory, $"rebuild-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var tee = new ColorTeeWriter(Console.Out, logPath);
            Console.SetOut(tee);
            Console.SetError(tee);

            ConsoleEx.Banner("DNGuard IL-Restorer 1.0 — IL reconstruction engine v0.8.6");
            ConsoleEx.Info("Dump    : " + dump);
            ConsoleEx.Info("Out     : " + output);

            var verbose = !cli.Has("--quiet");
            var prologue = cli.Value("--prologue", "strip")!;
            var eh = cli.Value("--eh", "flatten")!;
            var resolve = !cli.Has("--no-resolve");
            var fields = cli.Value("--fields", "high-confidence")!;
            int rc;

            if (Directory.Exists(Path.Combine(index, "by-token")) && !cli.Has("--from-corpus"))
            {
                rc = global::Program.RebuildFromIndex(
                    dump, index, output, Path.GetDirectoryName(dump)!,
                    verbose, prologue, eh, resolve, fields);
            }
            else if (corpus is not null && Directory.Exists(Path.Combine(corpus, "methods")))
            {
                ConsoleEx.Warn("Index missing; rebuilding directly from corpus.");
                rc = global::Program.RebuildFromCorpus(
                    new[] { corpus }, dump, output,
                    cli.Value("--namespace") ?? ws.TargetName,
                    Path.GetDirectoryName(dump)!, verbose, prologue, eh, resolve, fields);
            }
            else
            {
                ConsoleEx.Error("No valid index or corpus found.");
                ConsoleEx.Dim("Run DNGuard.Index.exe first.");
                ConsoleEx.PauseOnExit();
                return 3;
            }

            if (rc == 0)
            {
                ConsoleEx.Ok("Rebuilt  : " + output);
                File.WriteAllText(Path.Combine(ws.RebuiltDirectory, "latest.txt"), output);
            }
            ConsoleEx.PauseOnExit();
            return rc;
        }
        catch (Exception ex) { ConsoleEx.Error(ex.ToString()); ConsoleEx.PauseOnExit(); return 1; }
    }

    private static void Usage()
    {
        Console.WriteLine("""
DNGuard.Rebuild <target.exe>
  [--dump <dump>] [--index <index-dir>] [--out <rebuilt.dll>]
  [--from-corpus] [--corpus <session>] [--namespace <root-ns>]
  [--prologue strip|report|off] [--eh flatten|skip]
  [--no-resolve] [--fields high-confidence|off] [--quiet]

You can drag the target EXE onto DNGuard.Rebuild.exe.
By default, the dump, index, and output in _dnguard next to the target are used automatically.
""");
    }
}
