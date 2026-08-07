namespace DNGuard.Common;

public static class ToolLocator
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "obj", "packages", "node_modules"
    };

    public static string? Find(WorkspaceLayout ws, string fileName, string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        var env = Environment.GetEnvironmentVariable("DNGUARD_TOOLS");
        var directCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "payloads", fileName),
            Path.Combine(AppContext.BaseDirectory, "capture-tools", fileName),
            Path.Combine(ws.ToolsDirectory, fileName),
            Path.Combine(ws.TargetDirectory, "DNGuardTools", fileName),
            string.IsNullOrWhiteSpace(env) ? "" : Path.Combine(env, fileName)
        };

        var direct = directCandidates.FirstOrDefault(
            x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
        if (direct is not null)
            return Path.GetFullPath(direct);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var recursiveRoots = new[]
        {
            @"C:\tool_dng",
            @"C:\Tools",
            string.IsNullOrWhiteSpace(profile) ? "" : Path.Combine(profile, "Downloads")
        };

        foreach (var root in recursiveRoots)
        {
            var found = FindNewestRecursive(root, fileName, maxDepth: 7);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static string? FindNewestRecursive(string root, string fileName, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        var found = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    found.Add(candidate);

                if (depth >= maxDepth)
                    continue;

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (SkippedDirectories.Contains(name))
                        continue;
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return found
            .OrderByDescending(path =>
            {
                try { return File.GetLastWriteTimeUtc(path); }
                catch { return DateTime.MinValue; }
            })
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }
}
