using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DNGuard.Common;

namespace DNGuard.Capture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var cli = new Cli(args);
        var shouldPause = !cli.Has("--no-pause") && !Console.IsInputRedirected;
        var exitCode = 0;

        try
        {
            exitCode = await RunAsync(cli);
        }
        catch (OperationCanceledException)
        {
            ConsoleEx.Warn("Capture cancelled.");
            exitCode = 130;
        }
        catch (Exception ex)
        {
            ConsoleEx.Error(ex.ToString());
            exitCode = 1;
        }
        finally
        {
            if (shouldPause)
            {
                Console.WriteLine();
                ConsoleEx.Dim($"Exit code: {exitCode} (0x{unchecked((uint)exitCode):X8})");
                ConsoleEx.Dim("Press any key to exit...");
                try { Console.ReadKey(intercept: true); } catch { }
            }
        }

        return exitCode;
    }

    private static async Task<int> RunAsync(Cli cli)
    {
        if (cli.Has("--help") || cli.Has("-h"))
        {
            Usage();
            return 0;
        }

        var target = cli.Value("--target") ?? cli.FirstFile();
        if (string.IsNullOrWhiteSpace(target))
        {
            Usage();
            return 2;
        }

        var ws = WorkspaceLayout.FromTarget(target);
        ws.EnsureAll();
        Console.Title = $"DNGuard JIT-Dumper — {ws.TargetName}";
        ConsoleEx.Banner("DNGuard JIT-Dumper 1.0.1 — runtime IL capture via JIT boundary");

        // Capture only needs a managed module MVID. Prefer the dump when present,
        // but fall back to the protected target instead of aborting immediately.
        var dump = ws.DetectDump(cli.Value("--dump"));
        var mvidSource = dump ?? ws.TargetPath;
        var mvid = ReadMvid(mvidSource);
        var verbose = cli.Has("--verbose");

        ConsoleEx.Info("Target  : " + ws.TargetPath);
        if (verbose) ConsoleEx.Info("MVID    : " + mvid);

        // Managed PE bitness: a 32-bit target cannot host the x64 shim (LoadLibraryW returns 0).
        var bitness = ReadManagedBitness(ws.TargetPath);
        if (bitness is not null && !bitness.Value.is64)
        {
            ConsoleEx.Error("Target is 32-bit (x86) but the JIT shim is x64 — injection will fail.");
            ConsoleEx.Dim("Rebuild the target as x64 / AnyCPU without Prefer32Bit, or use an x86 shim build.");
            ConsoleEx.Dim($"  corflags: ILONLY={(bitness.Value.ilOnly ? 1 : 0)} 32BITREQUIRED={(bitness.Value.req32 ? 1 : 0)} 32BITPREFERRED={(bitness.Value.pref32 ? 1 : 0)} machine=0x{bitness.Value.machine:X4}");
            return 3;
        }

        // DNGuard version marker — detect from embedded runtime strings in the target exe
        try
        {
            var marker = ScanForDnGuardMarker(ws.TargetPath);
            if (marker is not null) ConsoleEx.Info("DNGuard : " + marker);
        }
        catch { }
        if (dump is null)
        {
            ConsoleEx.Warn("Dump not found — capture will use the target for MVID; dump is only needed at Rebuild.");
        }
        else if (verbose) ConsoleEx.Dim("Dump    : " + dump);

        // Static virtualized-stub baseline: the count of methods DNGuard actually virtualized,
        // which is the correct coverage denominator (NOT total MethodDef count).
        if (dump is not null)
        {
            try
            {
                var bl = global::Program.BuildVirtualizedBaseline(dump);
                global::Program.SaveVirtualizedBaseline(bl, ws.Root);
                ConsoleEx.Ok($"Baseline  : {bl.VirtualizedCandidates:N0} virtualized methods "
                    + $"(force-preparable={bl.ForcePreparable:N0}, open-generic={bl.OpenGenericCandidates:N0})");
                BaselineCache.Set(bl);
            }
            catch (Exception ex)
            {
                ConsoleEx.Warn("Could not compute virtualized baseline: " + ex.Message);
            }
        }

        var launcher = ToolLocator.Find(ws, "DNGuardJitLauncher.exe", cli.Value("--launcher"));
        var shim = ToolLocator.Find(ws, "DNGuardJitShim.dll", cli.Value("--shim"));
        var forceJit = ToolLocator.Find(ws, "ForceJit.dll", cli.Value("--forcejit"));

        var missing = new[]
        {
            ("DNGuardJitLauncher.exe", launcher),
            ("DNGuardJitShim.dll", shim),
            ("ForceJit.dll", forceJit)
        }.Where(x => x.Item2 is null).Select(x => x.Item1).ToArray();

        if (missing.Length > 0)
        {
            ConsoleEx.Error("Missing capture payloads: " + string.Join(", ", missing));
            ConsoleEx.Dim("Pass explicitly: --launcher <exe> --shim <dll> --forcejit <dll>");
            return 3;
        }

        if (verbose)
        {
            ConsoleEx.Dim("Launcher: " + launcher);
            ConsoleEx.Dim("Shim    : " + shim);
            ConsoleEx.Dim("ForceJit: " + forceJit);
        }

        var session = ws.CreateCaptureSession();
        var launcherLog = Path.Combine(session, "capture.log");
        var forceLog = Path.Combine(session, "forcejit.log");
        var forceCheckpoint = Path.Combine(session, "forcejit.ckpt");
        var forceDone = Path.Combine(session, "forcejit.done");
        var warmup = Math.Max(0, cli.Int("--warmup", 30000));
        var passes = Math.Max(1, cli.Int("--passes", 2));
        var autoExit = cli.Has("--auto-exit") && !cli.Has("--no-auto-exit");

        ConsoleEx.Info("Session   : " + session);
        ConsoleEx.Info($"Config    : warmup={warmup}ms passes={passes} auto-exit={(autoExit ? "on" : "off")}");

        var manifest = new
        {
            target = ws.TargetPath,
            dump,
            mvidSource,
            mvid,
            session,
            launcher,
            shim,
            forceJit,
            warmup,
            passes,
            autoExit,
            startedAt = DateTimeOffset.Now
        };
        File.WriteAllText(
            Path.Combine(session, "session.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var psi = new ProcessStartInfo(launcher!)
        {
            WorkingDirectory = Path.GetDirectoryName(launcher!) ?? ws.TargetDirectory
        };
        psi.ArgumentList.Add("--host"); psi.ArgumentList.Add(ws.TargetPath);
        psi.ArgumentList.Add("--shim"); psi.ArgumentList.Add(shim!);
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(session);
        psi.ArgumentList.Add("--mode"); psi.ArgumentList.Add("clrjit-direct");

        psi.Environment["DOTNET_STARTUP_HOOKS"] = forceJit!;
        psi.Environment["DG_FORCEJIT_MVID"] = mvid;
        psi.Environment["DG_FORCEJIT_NAME"] = cli.Value("--target-name") ?? ws.TargetName;
        psi.Environment["DG_FORCEJIT_DRY"] = cli.Has("--dry-run") ? "1" : "0";
        psi.Environment["DG_FORCEJIT_GENERICS"] = cli.Has("--no-generics") ? "0" : "1";
        psi.Environment["DG_FORCEJIT_AUTOEXIT"] = "0";   // Capture owns host lifetime; shim/ForceJit never auto-exit
        psi.Environment["DG_FORCEJIT_RESUME"] = cli.Has("--no-resume") ? "0" : "1";
        psi.Environment["DG_FORCEJIT_WARMUP_MS"] = warmup.ToString();
        psi.Environment["DG_FORCEJIT_PASSES"] = passes.ToString();
        psi.Environment["DG_FORCEJIT_LOG"] = forceLog;
        psi.Environment["DG_FORCEJIT_CHECKPOINT"] = forceCheckpoint;
        psi.Environment["DG_FORCEJIT_DONE"] = forceDone;

        CopyOptional(cli, psi, "--rid-min", "DG_FORCEJIT_RID_MIN");
        CopyOptional(cli, psi, "--rid-max", "DG_FORCEJIT_RID_MAX");
        CopyOptional(cli, psi, "--throttle-ms", "DG_FORCEJIT_THROTTLE_MS");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            ConsoleEx.Warn("Stopping the entire process tree...");
        };

        using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var tailTask = TailFileAsync(forceLog, tailCts.Token);

        var methodsRoot = Path.Combine(session, "methods");
        System.Diagnostics.Process? hostProc = null;

        static bool HostAlive(System.Diagnostics.Process? p)
        {
            if (p is null) return true;   // not started yet
            try
            {
                using var check = System.Diagnostics.Process.GetProcessById(p.Id);
                return !check.HasExited;
            }
            catch { return false; }       // pid gone => exited
        }

        var baseline = BaselineCache.Get();
        var coverage = new LiveCoverageTracker(methodsRoot, baseline);
        var baselineTotal = baseline?.VirtualizedCandidates ?? 0;

        // Live corpus counter from the start; settles when SWEEP COMPLETE is seen AND the
        // methods dir count stays stable across polls; closes the host only with --auto-exit.
        var killerTask = Task.Run(async () =>
        {
            try
            {
                var sweepSeen = false;
                var lastCount = -1;
                var stablePolls = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    if (!sweepSeen && (File.Exists(forceDone) || LogContains(forceLog, "SWEEP COMPLETE")))
                    {
                        sweepSeen = true;
                        Console.WriteLine();
                        ConsoleEx.Ok("SWEEP COMPLETE detected — waiting for shim writer to drain...");
                    }

                    if (hostProc is not null && !HostAlive(hostProc)) break;   // host closed/died
                    if (sweepSeen && stablePolls >= 2) break;                      // settled

                    coverage.Update();
                    var count = coverage.TotalDirs;
                    if (count != lastCount)
                    {
                        stablePolls = 0;
                        if (baselineTotal > 0 && coverage.UniqueCaptured > 0)
                        {
                            var pct = coverage.UniqueCaptured * 100.0 / baselineTotal;
                            Console.Write($"\r    methods: {count,8:N0}   coverage: {coverage.UniqueCaptured,6:N0}/{baselineTotal:N0} ({pct,5:F1}%)   ");
                        }
                        else
                        {
                            Console.Write($"\r    methods captured: {count:N0}   ");
                        }
                    }
                    else stablePolls++;
                    lastCount = count;

                    await Task.Delay(2000, cts.Token);
                }

                coverage.Update();   // final drain
                Console.Write("\r".PadRight(95) + "\r");
                ConsoleEx.Ok($"Corpus settled: {Math.Max(0, coverage.TotalDirs):N0} method dirs (writer drained)");

                if (autoExit && hostProc is not null && HostAlive(hostProc))
                {
                    ConsoleEx.Info("Auto-exit: closing target host.");
                    try { hostProc.CloseMainWindow(); } catch { }
                    try { if (!hostProc.WaitForExit(5000)) hostProc.Kill(entireProcessTree: true); }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        int exit;
        try
        {
            exit = await ProcessRunner.RunAsync(psi, launcherLog, cts.Token, p => hostProc = p);
        }
        finally
        {
            tailCts.Cancel();
            try { await tailTask; } catch (OperationCanceledException) { }
            try { await killerTask; } catch (OperationCanceledException) { }
        }

    // Final settle (host may have been closed manually before SWEEP COMPLETE appeared).
        var settleFinal = await WaitForCorpusSettleAsync(methodsRoot, CancellationToken.None, 2, 30);
        var count = settleFinal;
        var sweepComplete = File.Exists(forceDone) || LogContains(forceLog, "SWEEP COMPLETE");

        // Coverage against the static virtualized baseline (unique baseline tokens captured).
        if (baseline is not null)
        {
            coverage.Update();   // ensure tracker ingested everything the settle pass wrote
            var captured = coverage.UniqueCaptured;
            var pct = baselineTotal > 0 ? captured * 100.0 / baselineTotal : 0;
            var status = pct >= 95 ? "CONVERGED_HIGH"
                       : pct >= 70 ? "GOOD"
                       : "STALLED_LOW — rerun capture with more UI exercise";
            ConsoleEx.Ok($"Coverage  : {captured:N0} / {baselineTotal:N0}  ({pct:F1}%)  {status}");
            if (pct < 95) ConsoleEx.Dim("Tip: exercise more UI in the next capture session to raise coverage.");
        }

        ws.SetCurrentCapture(session, exit, count, sweepComplete);

        Console.WriteLine();
        ConsoleEx.Info($"Summary : {count:N0} methods saved, sweep={sweepComplete}");

        if (count == 0)
        {
            ConsoleEx.Error("Capture finished with no methods.");
            if (!File.Exists(forceLog))
                ConsoleEx.Error("ForceJit didn't create forcejit.log: the startup hook may not have loaded.");
            if (File.Exists(forceCheckpoint))
                ConsoleEx.Warn("Checkpoint: " + SafeRead(forceCheckpoint));
            return exit == 0 ? 5 : exit;
        }

        if (!sweepComplete)
        {
            ConsoleEx.Warn("Corpus contains data, but ForceJit has not yet reported SWEEP COMPLETE.");
        }

        ConsoleEx.Ok($"Captured  : {session}");
        ConsoleEx.Dim("Next step: drag the target onto DNGuard.Index.exe");
        return exit;
    }

    // ── live incremental index: unique baseline tokens + observed dir count ─
    sealed class LiveCoverageTracker
    {
        private readonly string _methodsRoot;
        private readonly HashSet<uint>? _baseline;
        private readonly HashSet<uint> _captured = new();
        private long _lastDirNum = 0;
        private int _totalDirs = 0;

        public LiveCoverageTracker(string methodsRoot, global::Program.VirtualizedBaseline? baseline)
        {
            _methodsRoot = methodsRoot;
            _baseline = baseline is null || baseline.VirtualizedTokens.Count == 0
                ? null
                : new HashSet<uint>(baseline.VirtualizedTokens);
        }

        public int UniqueCaptured => _captured.Count;
        public int TotalDirs => _totalDirs;

        public void Update()
        {
            if (!Directory.Exists(_methodsRoot)) return;
            foreach (var path in Directory.EnumerateDirectories(_methodsRoot))
            {
                var name = Path.GetFileName(path);
                if (!long.TryParse(name, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var num)) { _totalDirs++; continue; }
                if (num <= _lastDirNum) continue;      // already ingested
                _lastDirNum = num;
                _totalDirs++;
                if (_baseline is null) continue;
                var identityPath = Path.Combine(path, "identity.json");
                if (!File.Exists(identityPath)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(identityPath));
                    if (!doc.RootElement.TryGetProperty("metadataToken", out var tokProp)) continue;
                    var tokText = tokProp.GetString();
                    if (string.IsNullOrWhiteSpace(tokText)) continue;
                    if (!uint.TryParse(tokText.Replace("0x", "").Replace("0X", ""),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var tok)) continue;
                    if (_baseline.Contains(tok)) _captured.Add(tok);
                }
                catch { }
            }
        }
    }

    // ── baseline helpers ────────────────────────────────────────────────────
    static class BaselineCache
    {
        private static global::Program.VirtualizedBaseline? _b;
        public static void Set(global::Program.VirtualizedBaseline b) => _b = b;
        public static global::Program.VirtualizedBaseline? Get() => _b;
    }

    private static async Task<int> WaitForCorpusSettleAsync(
        string methodsRoot, CancellationToken cancellationToken, int stablePollsRequired = 2, int maxPolls = 60)
    {
        var lastCount = -1;
        var stablePolls = 0;
        for (var poll = 0; poll < maxPolls; poll++)
        {
            var count = Directory.Exists(methodsRoot)
                ? Directory.EnumerateDirectories(methodsRoot).Count()
                : 0;

            if (count == lastCount)
            {
                stablePolls++;
                if (stablePolls >= stablePollsRequired) return count;
            }
            else
            {
                stablePolls = 0;
                Console.Write($"\r    writer draining… {count:N0} method dirs   ");
            }
            lastCount = count;
            try { await Task.Delay(2000, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
        Console.Write($"\r".PadRight(60) + "\r");
        return lastCount < 0 ? 0 : lastCount;
    }

    private static void CopyOptional(Cli cli, ProcessStartInfo psi, string option, string environmentName)
    {
        var value = cli.Value(option);
        if (!string.IsNullOrWhiteSpace(value))
            psi.Environment[environmentName] = value;
    }

    private static async Task TailFileAsync(string path, CancellationToken cancellationToken)
    {
        long position = 0;
        string? lastLine = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    if (position > stream.Length)
                        position = 0;
                    stream.Position = position;

                    using var reader = new StreamReader(stream);
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync(cancellationToken);
                        if (line is null || line == lastLine)
                            continue;
                        lastLine = line;
                        PrintForceJit(line);
                    }
                    position = stream.Position;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            await Task.Delay(250, cancellationToken);
        }
    }

    private static void PrintForceJit(string line)
    {
        var old = Console.ForegroundColor;
        if (line.Contains("SWEEP COMPLETE", StringComparison.OrdinalIgnoreCase))
            Console.ForegroundColor = ConsoleColor.Green;
        else if (line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("exception", StringComparison.OrdinalIgnoreCase))
            Console.ForegroundColor = ConsoleColor.Red;
        else if (line.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("skip", StringComparison.OrdinalIgnoreCase))
            Console.ForegroundColor = ConsoleColor.Yellow;
        else if (line.Contains("method", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("prepare", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("pass", StringComparison.OrdinalIgnoreCase))
            Console.ForegroundColor = ConsoleColor.Cyan;
        else
            Console.ForegroundColor = ConsoleColor.DarkGray;

        Console.WriteLine("[forcejit] " + line);
        Console.ForegroundColor = old;
    }

    private static bool LogContains(string path, string text)
    {
        try
        {
            return File.Exists(path) &&
                   File.ReadLines(path).Any(x => x.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return "?"; }
    }

    // (bool is64, bool ilOnly, bool req32, bool pref32, ushort machine)? — null if unmanaged/unreadable
    private static (bool is64, bool ilOnly, bool req32, bool pref32, ushort machine)? ReadManagedBitness(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var headers = pe.PEHeaders;
            var cf = headers.CorHeader.Flags;
            var machine = (ushort)headers.CoffHeader.Machine;
            var ilOnly = (cf & CorFlags.ILOnly) != 0;
            var req32 = (cf & CorFlags.Requires32Bit) != 0;
            var pref32 = (cf & CorFlags.Prefers32Bit) != 0;
            // x64 only if PE32+ machine is AMD64, or AnyCPU without 32-bit preference
            var is64 = headers.PEHeader.Magic == PEMagic.PE32Plus;
            return (is64, ilOnly, req32, pref32, machine);
        }
        catch { return null; }
    }

    private static string? ScanForDnGuardMarker(string path)
    {
        // read a bounded chunk around resources / version strings (avoid full 200MB scan)
        var data = new byte[16 * 1024 * 1024];   // read first 16MB — markers live in early sections
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Read(data, 0, data.Length);

        var text = System.Text.Encoding.Latin1.GetString(data);
        if (text.Contains("DNGuard HVM demo version")) return "HVM 4.9.6 (demo/trial variant)";
        if (text.Contains("DNGuard HVM")) return "HVM (full variant)";
        if (text.Contains("DNGuard")) return "DNGuard detected";
        return null;
    }

    private static string ReadMvid(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new InvalidDataException("File has no .NET metadata: " + assemblyPath);
        var reader = pe.GetMetadataReader();
        var module = reader.GetModuleDefinition();
        return reader.GetGuid(module.Mvid).ToString();
    }

    private static void Usage()
    {
        Console.WriteLine("""
DNGuard.Capture <target.exe>
  [--dump <dump.exe>] [--launcher <exe>] [--shim <dll>] [--forcejit <dll>]
  [--warmup 30000] [--passes 2] [--auto-exit] [--no-generics]
  [--rid-min N] [--rid-max N] [--throttle-ms N] [--no-resume]
  [--dry-run] [--no-pause] [--verbose]

You can drag the target EXE onto DNGuard.Capture.exe.
The host stays open until ForceJit reports SWEEP COMPLETE and the shim writer has drained
("Corpus settled"). With --auto-exit, Capture closes the host itself once it is safe.
Output: <target-dir>\_dnguard\<target>\capture\sessions\<timestamp>\
""");
    }
}
