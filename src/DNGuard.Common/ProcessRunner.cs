using System.Diagnostics;

namespace DNGuard.Common;

public static class ProcessRunner
{
    public static async Task<int> RunAsync(ProcessStartInfo psi, string logPath, CancellationToken cancellationToken, Action<Process>? onStarted = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        await using var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = false;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Print(e.Data, false); lock (log) log.WriteLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { Print(e.Data, true); lock (log) log.WriteLine(e.Data); } };

        if (!process.Start()) throw new InvalidOperationException("Could not start process: " + psi.FileName);
        onStarted?.Invoke(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static void Print(string line, bool error)
    {
        var old = Console.ForegroundColor;
        if (error || line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase)) Console.ForegroundColor = ConsoleColor.Red;
        else if (line.Contains("SWEEP COMPLETE", StringComparison.OrdinalIgnoreCase) || line.Contains("captured", StringComparison.OrdinalIgnoreCase)) Console.ForegroundColor = ConsoleColor.Green;
        else if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase)) Console.ForegroundColor = ConsoleColor.Yellow;
        else if (line.Contains("method", StringComparison.OrdinalIgnoreCase) || line.Contains("JIT", StringComparison.OrdinalIgnoreCase)) Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(line);
        Console.ForegroundColor = old;
    }
}
