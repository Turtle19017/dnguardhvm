using System.Text.Json;

namespace DNGuard.Common;

public sealed class WorkspaceLayout
{
    public string TargetPath { get; }
    public string TargetDirectory { get; }
    public string TargetFileName { get; }
    public string TargetName { get; }
    public string Root { get; }
    public string CaptureRoot => Path.Combine(Root, "capture");
    public string CaptureSessions => Path.Combine(CaptureRoot, "sessions");
    public string CurrentCaptureFile => Path.Combine(CaptureRoot, "current.json");
    public string IndexDirectory => Path.Combine(Root, "index");
    public string RebuiltDirectory => Path.Combine(Root, "rebuilt");
    public string StringsDirectory => Path.Combine(Root, "strings");
    public string ReportsDirectory => Path.Combine(Root, "reports");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string ToolsDirectory => Path.Combine(Root, "tools");
    public string DumpDirectory => Path.Combine(Root, "dump");
    public string DefaultDumpPath => Path.Combine(DumpDirectory, TargetFileName);
    // Rebuilt outputs sit NEXT TO the original exe (so a drag-and-drop user sees the result immediately)
    public string DefaultRebuiltPath => Path.Combine(TargetDirectory, TargetName + ".rebuilt.exe");
    public string DefaultStringsPath => Path.Combine(TargetDirectory, TargetName + ".rebuilt.strings.exe");

    private WorkspaceLayout(string targetPath)
    {
        TargetPath = Path.GetFullPath(targetPath);
        TargetDirectory = Path.GetDirectoryName(TargetPath)!;
        TargetFileName = Path.GetFileName(TargetPath);
        TargetName = Path.GetFileNameWithoutExtension(TargetPath);
        Root = Path.Combine(TargetDirectory, "_dnguard", TargetName);
    }

    public static WorkspaceLayout FromTarget(string targetPath)
    {
        if (!File.Exists(targetPath)) throw new FileNotFoundException("Target not found", targetPath);
        return new WorkspaceLayout(targetPath);
    }

    public void EnsureAll()
    {
        foreach (var path in new[] { Root, CaptureRoot, CaptureSessions, IndexDirectory, RebuiltDirectory, StringsDirectory, ReportsDirectory, LogsDirectory, ToolsDirectory, DumpDirectory })
            Directory.CreateDirectory(path);
    }

    public string CreateCaptureSession()
    {
        EnsureAll();
        var baseName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(CaptureSessions, baseName);
        var suffix = 1;
        while (Directory.Exists(path)) path = Path.Combine(CaptureSessions, $"{baseName}-{suffix++}");
        Directory.CreateDirectory(path);
        return path;
    }

    public void SetCurrentCapture(string sessionPath, int exitCode, int methodDirectories, bool sweepComplete = false)
    {
        Directory.CreateDirectory(CaptureRoot);
        var payload = new
        {
            target = TargetPath,
            session = Path.GetFullPath(sessionPath),
            exitCode,
            methodDirectories,
            sweepComplete,
            updatedAt = DateTimeOffset.Now
        };
        File.WriteAllText(CurrentCaptureFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? DetectCurrentCapture()
    {
        try
        {
            if (File.Exists(CurrentCaptureFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(CurrentCaptureFile));
                if (doc.RootElement.TryGetProperty("session", out var p))
                {
                    var session = p.GetString();
                    if (!string.IsNullOrWhiteSpace(session) && Directory.Exists(Path.Combine(session, "methods")))
                        return session;
                }
            }
        }
        catch { }

        if (!Directory.Exists(CaptureSessions)) return null;
        return Directory.EnumerateDirectories(CaptureSessions)
            .Where(x => Directory.Exists(Path.Combine(x, "methods")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public string? DetectDump(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return Path.GetFullPath(explicitPath);

        var candidates = new List<string>
        {
            DefaultDumpPath,
            Path.Combine(TargetDirectory, "Dumps", TargetFileName),
            Path.Combine(TargetDirectory, "dumps", TargetFileName),
            Path.Combine(TargetDirectory, TargetName + ".dumped" + Path.GetExtension(TargetPath)),
            Path.Combine(TargetDirectory, TargetName + ".dump" + Path.GetExtension(TargetPath))
        };

        if (Path.GetExtension(TargetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(TargetDirectory, "Dumps", TargetName + ".dll"));
            candidates.Add(Path.Combine(DumpDirectory, TargetName + ".dll"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public string? DetectLatestRebuilt()
    {
        // prefer the default location next to the original exe, then fall back to the rebuilt dir
        var preferred = DefaultRebuiltPath;
        if (File.Exists(preferred)) return preferred;
        if (!Directory.Exists(RebuiltDirectory)) return null;
        return Directory.EnumerateFiles(RebuiltDirectory, "*.exe", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(RebuiltDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
