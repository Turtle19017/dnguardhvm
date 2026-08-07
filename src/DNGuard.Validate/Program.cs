using DNGuard.Common;

namespace DNGuard.Validate;

internal static class EntryPoint
{
    private static int Main(string[] args)
    {
        var cli = new Cli(args);
        if (cli.Has("--help") || cli.Has("-h")) { Usage(); return 0; }
        var target = cli.Value("--target") ?? cli.FirstFile();
        var explicitAssembly = cli.Value("--assembly");
        if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(explicitAssembly)) { Usage(); return 2; }

        try
        {
            WorkspaceLayout? ws = null;
            string? assembly;
            if (!string.IsNullOrWhiteSpace(explicitAssembly))
            {
                assembly = Path.GetFullPath(explicitAssembly);
            }
            else
            {
                ws = WorkspaceLayout.FromTarget(target!);
                ws.EnsureAll();
                assembly = ws.DetectLatestRebuilt();
            }

            if (assembly is null || !File.Exists(assembly))
            {
                ConsoleEx.Error("Không tìm thấy DLL rebuilt để validate.");
                return 3;
            }

            var logDir = ws?.LogsDirectory ?? Path.GetDirectoryName(assembly)!;
            var logPath = Path.Combine(logDir, $"validate-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            using var tee = new ColorTeeWriter(Console.Out, logPath);
            Console.SetOut(tee);
            Console.SetError(tee);

            ConsoleEx.Banner("DNGuard Validate 1.0 — post-write semantic checks");
            ConsoleEx.Info("Assembly: " + assembly);
            var rc = global::Program.ValidateStandalone(assembly);
            ConsoleEx.Info("Reports : " + Path.GetDirectoryName(assembly));
            if (rc == 0) ConsoleEx.Ok("Assembly không còn core issue theo validator.");
            else ConsoleEx.Warn("Validator hoàn tất và còn residual; xem semantic-validation.txt.");
            return rc;
        }
        catch (Exception ex) { ConsoleEx.Error(ex.ToString()); return 1; }
    }

    private static void Usage()
    {
        Console.WriteLine("""
DNGuard.Validate <target.exe>
DNGuard.Validate --assembly <rebuilt.dll>

Khi nhận target EXE, tool tự chọn DLL mới nhất trong:
  <target-dir>\_dnguard\<target>\rebuilt\
""");
    }
}
