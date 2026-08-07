using System.Text;

namespace DNGuard.Common;

public static class ConsoleEx
{
    public static void Banner(string title)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(new string('═', Math.Min(78, Math.Max(30, title.Length + 8))));
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  " + title);
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(new string('═', Math.Min(78, Math.Max(30, title.Length + 8))));
        Console.ForegroundColor = old;
    }

    public static void Info(string text) => Write(ConsoleColor.Cyan, "[*] " + text);
    public static void Ok(string text) => Write(ConsoleColor.Green, "[+] " + text);
    public static void Warn(string text) => Write(ConsoleColor.Yellow, "[!] " + text);
    public static void Error(string text) => Write(ConsoleColor.Red, "[x] " + text, true);
    public static void Dim(string text) => Write(ConsoleColor.DarkGray, "    " + text);

    public static bool Interactive => !Console.IsInputRedirected;

    public static void PauseOnExit()
    {
        if (!Interactive) return;
        Console.WriteLine();
        Dim("Press any key to exit...");
        try { Console.ReadKey(intercept: true); } catch { }
    }

    public static void Write(ConsoleColor color, string text, bool stderr = false)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        if (stderr) Console.Error.WriteLine(text); else Console.WriteLine(text);
        Console.ForegroundColor = old;
    }
}

public sealed class ColorTeeWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly StreamWriter _log;
    private readonly object _gate = new();
    public override Encoding Encoding => _console.Encoding;

    public ColorTeeWriter(TextWriter console, string logPath)
    {
        _console = console;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        _log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public override void Write(char value)
    {
        lock (_gate) { _console.Write(value); _log.Write(value); }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_gate) { _console.Write(value); _log.Write(value); }
    }

    public override void WriteLine(string? value)
    {
        value ??= "";
        lock (_gate)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = Pick(value);
            _console.WriteLine(value);
            Console.ForegroundColor = old;
            _log.WriteLine(value);
        }
    }

    private static ConsoleColor Pick(string line)
    {
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.StartsWith("[x]")) return ConsoleColor.Red;
        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase) || line.Contains("unmap=", StringComparison.OrdinalIgnoreCase) && !line.Contains("unmap=0")) return ConsoleColor.Yellow;
        if (line.Contains(" OK", StringComparison.Ordinal) || line.StartsWith("[+]")) return ConsoleColor.Green;
        if (line.StartsWith("===", StringComparison.Ordinal) || line.Contains("DONE", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.Magenta;
        if (line.StartsWith("[*]", StringComparison.Ordinal) || line.Contains("STEP", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.Cyan;
        return ConsoleColor.Gray;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _log.Dispose();
        base.Dispose(disposing);
    }
}
