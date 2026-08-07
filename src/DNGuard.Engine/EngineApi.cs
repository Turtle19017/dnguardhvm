using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

public partial class Program
{
    public static int RebuildFromIndex(
        string module,
        string index,
        string output,
        string dependencyDirectory,
        bool verbose = true,
        string prologueMode = "strip",
        string ehMode = "flatten",
        bool resolveReferences = true,
        string fieldMode = "high-confidence")
    {
        var args = new List<string>
        {
            "--module", module,
            "--index", index,
            "--out", output,
            "--dep-dir", dependencyDirectory,
            "--prologue-mode", prologueMode,
            "--eh-mode", ehMode,
            "--resolve-refs", resolveReferences ? "on" : "off",
            "--retarget-object-fields", fieldMode
        };
        if (verbose) args.Add("--verbose");
        return Main(args.ToArray());
    }

    public static int RebuildFromCorpus(
        IReadOnlyCollection<string> corpora,
        string module,
        string output,
        string targetNamespace,
        string dependencyDirectory,
        bool verbose = true,
        string prologueMode = "strip",
        string ehMode = "flatten",
        bool resolveReferences = true,
        string fieldMode = "high-confidence")
    {
        var args = new List<string>();
        foreach (var corpus in corpora)
        {
            args.Add("--corpus");
            args.Add(corpus);
        }
        args.AddRange(new[]
        {
            "--module", module,
            "--out", output,
            "--target-ns", targetNamespace,
            "--dep-dir", dependencyDirectory,
            "--prologue-mode", prologueMode,
            "--eh-mode", ehMode,
            "--resolve-refs", resolveReferences ? "on" : "off",
            "--retarget-object-fields", fieldMode
        });
        if (verbose) args.Add("--verbose");
        return RunPipeline(args.ToArray());
    }

    public static int RunPipelineFromApi(string[] args)
    {
        return global::Program.RunPipeline(args);
    }

    public static int RunStringDecryptFromApi(string rebuiltModule, string host, string hook, string output,
        bool verbose = true, int timeoutSeconds = 180)
    {
        string mvid = null;
        try { using var mod = dnlib.DotNet.ModuleDefMD.Load(rebuiltModule); mvid = mod.Mvid?.ToString(); } catch { }
        return global::Program.RunStringDecrypt(
            rebuiltModule, host, mvid, hook, verbose, timeoutSeconds.ToString(), output);
    }

    // ===== STEP 1+2: static virtualized-stub baseline from the dump =====
    // Detect DNGuard HVM stub bodies ("DNGuard Runtime library not loaded" + throw) in the
    // dumped module and emit the authoritative list of tokens that CAN be captured at the
    // JIT boundary. This is the correct coverage denominator (NOT total MethodDef count).
    public sealed class VirtualizedBaseline
    {
        public int TotalMethodsWithBody;
        public int VirtualizedCandidates;
        public int ForcePreparable;
        public int OpenGenericCandidates;
        public string DumpSha256 = "";
        public string ModuleMvid = "";
        public string DetectorVersion = "stub-v1";
        public List<uint> VirtualizedTokens = new();
    }

    public static VirtualizedBaseline BuildVirtualizedBaseline(string dumpPath)
    {
        if (!File.Exists(dumpPath)) throw new FileNotFoundException("Dump module not found", dumpPath);
        using var mod = dnlib.DotNet.ModuleDefMD.Load(dumpPath);
        var result = new VirtualizedBaseline
        {
            ModuleMvid = mod.Mvid?.ToString() ?? "",
            DumpSha256 = ComputeSha256(dumpPath)
        };

        foreach (var type in mod.GetTypes())
        {
            bool typeOpenGeneric = type.GenericParameters.Count > 0;
            foreach (var m in type.Methods)
            {
                if (!m.HasBody) continue;
                result.TotalMethodsWithBody++;

                if (!IsDnGuardStubBodyBaseline(m.Body)) continue;
                result.VirtualizedCandidates++;
                result.VirtualizedTokens.Add(m.MDToken.Raw);

                bool openGeneric = m.HasGenericParameters || typeOpenGeneric;
                if (openGeneric) result.OpenGenericCandidates++;
                else if (!m.IsAbstract && !m.IsPinvokeImpl) result.ForcePreparable++;
            }
        }
        return result;
    }

    static bool IsDnGuardStubBodyBaseline(dnlib.DotNet.Emit.CilBody body)
    {
        if (body == null) return false;
        var live = body.Instructions.Where(i => i.OpCode.Code != dnlib.DotNet.Emit.Code.Nop).ToList();
        if (live.Count < 3 || live[0].OpCode.Code != dnlib.DotNet.Emit.Code.Ldstr) return false;
        string text = live[0].Operand as string;
        return text != null && text.IndexOf("DNGuard Runtime library not loaded", StringComparison.OrdinalIgnoreCase) >= 0
            && live.Any(i => i.OpCode.Code == dnlib.DotNet.Emit.Code.Throw);
    }

    static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
    }

    public static void SaveVirtualizedBaseline(VirtualizedBaseline baseline, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var payload = new
        {
            detectorVersion = baseline.DetectorVersion,
            moduleMvid = baseline.ModuleMvid,
            dumpSha256 = baseline.DumpSha256,
            totalMethodsWithBody = baseline.TotalMethodsWithBody,
            virtualizedCandidates = baseline.VirtualizedCandidates,
            forcePreparable = baseline.ForcePreparable,
            openGenericCandidates = baseline.OpenGenericCandidates,
            virtualizedTokens = baseline.VirtualizedTokens.OrderBy(t => t).Select(t => $"0x{t:X8}").ToArray(),
            computedAt = DateTimeOffset.Now
        };
        File.WriteAllText(
            Path.Combine(targetDirectory, "baseline.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static int ValidateStandalone(string assemblyPath)
    {
        try
        {
            var rebuiltTokens = new List<uint>();
            var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? ".";
            var rebuiltReport = Path.Combine(directory, "rebuilt.txt");
            if (File.Exists(rebuiltReport))
            {
                foreach (var raw in File.ReadLines(rebuiltReport))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
                    var tokenText = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (uint.TryParse(tokenText.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var token))
                        rebuiltTokens.Add(token);
                }
            }

            var summary = ValidateWrittenModule(assemblyPath, rebuiltTokens);
            Console.WriteLine($"[*] methods={summary.Methods} structural={summary.StructurallyValid} core={summary.SemanticCoreClean} strict={summary.SemanticStrictClean}");
            Console.WriteLine($"[*] invalid={summary.InvalidMetadataOperands} stubs={summary.DnGuardStubs} open-generic={summary.OpenGenericLocals} initobj={summary.InitobjMismatches}");
            Console.WriteLine($"[*] object={summary.ObjectTypeOperands} proven={summary.ProvenObjectOperands} fallback={summary.ObjectFallbackOperands}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("validation failed: " + ex.Message);
            return 3;
        }
    }
    public static int BuildIndex(
        IReadOnlyCollection<string> corpora,
        string outputDirectory,
        string targetNamespace,
        bool clean = true,
        bool verbose = true)
    {
        try
        {
            if (clean && Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);
            Directory.CreateDirectory(outputDirectory);
            var byTokenDirectory = Path.Combine(outputDirectory, "by-token");
            Directory.CreateDirectory(byTokenDirectory);

            var counters = new Counters();
            var byKey = new Dictionary<(string scope, uint token), List<MRec>>();
            var scopeNamespaces = new Dictionary<string, Dictionary<string, int>>();
            var scopeCounts = new Dictionary<string, int>();

            Console.WriteLine("[*] scanning capture sessions...");
            foreach (var corpus in corpora)
            {
                var methodsRoot = Path.Combine(corpus, "methods");
                if (!Directory.Exists(methodsRoot))
                {
                    Console.WriteLine("[!] no methods directory: " + corpus);
                    continue;
                }
                int corpusDirs = 0;
                foreach (var directory in Directory.EnumerateDirectories(methodsRoot))
                {
                    counters.dirs++; corpusDirs++;
                    if ((corpusDirs & 0x3FF) == 0) Console.Write($"\r    scanning… {corpusDirs:N0} dirs");
                    var record = ReadRec(directory, null, counters);
                    if (record == null) continue;
                    var key = (record.scope, record.token);
                    if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<MRec>();
                    list.Add(record);
                    var ns = record.declNs ?? "";
                    if (!scopeNamespaces.TryGetValue(record.scope, out var nsCounts))
                        scopeNamespaces[record.scope] = nsCounts = new Dictionary<string, int>();
                    nsCounts[ns] = nsCounts.GetValueOrDefault(ns) + 1;
                    scopeCounts[record.scope] = scopeCounts.GetValueOrDefault(record.scope) + 1;
                }
                Console.Write($"\r    scanned {corpusDirs:N0} dirs from {Path.GetFileName(corpus)}".PadRight(70) + "\n");
            }
            Console.WriteLine($"[*] total: {counters.dirs:N0} method dirs, {scopeCounts.Count} module scopes detected");

            string targetScope = null;
            long bestRank = long.MinValue;
            var targetNeedle = (targetNamespace ?? "").ToLowerInvariant();
            foreach (var scope in scopeCounts.Keys)
            {
                long namespaceScore = scopeNamespaces[scope]
                    .Where(kv => (kv.Key ?? "").ToLowerInvariant().Contains(targetNeedle))
                    .Sum(kv => (long)kv.Value);
                var rank = namespaceScore * 1_000_000L + scopeCounts[scope];
                if (rank > bestRank) { bestRank = rank; targetScope = scope; }
            }
            if (targetScope == null)
            {
                Console.Error.WriteLine("[x] could not detect target module scope");
                return 3;
            }
            Console.WriteLine($"[+] target scope locked: {targetScope} ({scopeCounts.GetValueOrDefault(targetScope)} records)");

            var target = new Dictionary<uint, List<MRec>>();
            foreach (var pair in byKey)
            {
                if (!string.Equals(pair.Key.scope, targetScope, StringComparison.Ordinal)) continue;
                if (!target.TryGetValue(pair.Key.token, out var list)) target[pair.Key.token] = list = new List<MRec>();
                list.AddRange(pair.Value);
            }

            var callbacksByToken = new Dictionary<uint, List<CB>>();
            var globalReal = new Dictionary<string, uint>();
            var globalConflicts = new HashSet<string>();
            Console.WriteLine($"[*] building token maps for {target.Count:N0} unique tokens...");
            int cbDone = 0;
            foreach (var pair in target)
            {
                var callbacks = new List<CB>();
                foreach (var record in pair.Value)
                    callbacks.AddRange(ReadCallbacks(Path.Combine(record.dir, "callbacks.jsonl")));
                callbacksByToken[pair.Key] = callbacks;
                foreach (var callback in callbacks)
                {
                    var handle = callback.Handle();
                    var real = callback.RealToken();
                    if (handle == "0x0" || real == 0) continue;
                    if (globalReal.TryGetValue(handle, out var existing) && existing != real) globalConflicts.Add(handle);
                    else globalReal[handle] = real;
                }
                if ((++cbDone & 0x3FF) == 0) Console.Write($"\r    token maps… {cbDone:N0}/{target.Count:N0}");
            }
            foreach (var handle in globalConflicts) globalReal.Remove(handle);
            Console.Write($"\r[+] token maps built: {globalReal.Count:N0} handle→real mappings ({globalConflicts.Count} conflicts dropped)".PadRight(70) + "\n");
            Console.WriteLine("[*] writing by-token index...");

            var indexRows = new Dictionary<string, object>();
            var inconsistentRows = new Dictionary<string, object>();
            var methodsWithTokenMap = 0;
            var tokenMapEntries = 0;
            var methodsWithLocalsBlob = 0;
            var methodsWithEh = 0;
            var inconsistent = 0;
            var written = 0;

            foreach (var token in target.Keys.OrderBy(x => x))
            {
                var records = target[token];
                var usable = records.Where(x => !x.placeholder && x.ilSize > 0).ToList();
                if (usable.Count == 0) usable = records;
                var best = usable.OrderByDescending(x => x.ilSize).First();
                var sourceIl = Path.Combine(best.dir, "generated-il.bin");
                if (!File.Exists(sourceIl)) continue;

                var hashes = usable.Where(x => !string.IsNullOrWhiteSpace(x.ilSha))
                    .GroupBy(x => x.ilSha).ToDictionary(x => x.Key, x => x.Count());
                var consistent = hashes.Count <= 1;
                if (!consistent)
                {
                    inconsistent++;
                    inconsistentRows[$"0x{token:X8}"] = new
                    {
                        best.name,
                        chosenSize = best.ilSize,
                        variants = hashes.Select(x => new { sha = x.Key, count = x.Value }).ToArray()
                    };
                }

                var tokenDir = Path.Combine(byTokenDirectory, token.ToString("X8", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(tokenDir);
                File.Copy(sourceIl, Path.Combine(tokenDir, "il.bin"), true);

                var callbacks = callbacksByToken.GetValueOrDefault(token) ?? new List<CB>();
                var tokenMap = BuildTokenmapUnion(callbacks, globalReal);
                if (tokenMap.Count > 0)
                {
                    var mapObject = tokenMap.ToDictionary(
                        x => $"0x{x.Key:X8}",
                        x => (object)new { real = $"0x{x.Value:X8}" });
                    File.WriteAllText(Path.Combine(tokenDir, "tokenmap.json"),
                        JsonSerializer.Serialize(mapObject, new JsonSerializerOptions { WriteIndented = true }));
                    methodsWithTokenMap++;
                    tokenMapEntries += tokenMap.Count;
                }

                var hints = BuildHints(callbacks, tokenMap);
                if (hints.Count > 0)
                {
                    var hintObject = hints.ToDictionary(
                        x => $"0x{x.Key:X8}",
                        x => (object)new
                        {
                            x.Value.table,
                            x.Value.kind,
                            x.Value.ns,
                            x.Value.type,
                            x.Value.member,
                            extToken = x.Value.extToken,
                            x.Value.hasToken,
                            x.Value.dynamicOnly
                        });
                    File.WriteAllText(Path.Combine(tokenDir, "hints.json"),
                        JsonSerializer.Serialize(hintObject, new JsonSerializerOptions { WriteIndented = true }));
                }

                var ehByNumber = new SortedDictionary<int, EhMeta>();
                foreach (var record in records)
                    foreach (var eh in ReadEh(record.dir))
                        if (!ehByNumber.ContainsKey(eh.ehNumber)) ehByNumber[eh.ehNumber] = eh;
                if (ehByNumber.Count > 0) methodsWithEh++;

                var maxEhExpected = records.Select(x => x.ehCount).DefaultIfEmpty(0).Max();
                var maxLocals = records.Select(x => x.localsCount).DefaultIfEmpty(0).Max();
                var blob = records.FirstOrDefault(x => x.localsCbSig > 0 && File.Exists(Path.Combine(x.dir, "locals.bin")));
                if (blob != null)
                {
                    File.Copy(Path.Combine(blob.dir, "locals.bin"), Path.Combine(tokenDir, "locals.bin"), true);
                    methodsWithLocalsBlob++;
                }

                var meta = new
                {
                    token = $"0x{token:X8}",
                    best.name,
                    ilSize = best.ilSize,
                    maxStack = best.maxStack,
                    ehCount = maxEhExpected,
                    hasLocalsBlob = blob != null,
                    dynamicOnly = best.dynamicOnly,
                    locals = maxLocals > 0 ? new { count = maxLocals, cbSig = blob?.localsCbSig ?? 0 } : null,
                    eh = ehByNumber.Values.Select(e => new
                    {
                        e.ehNumber, e.flags, e.tryOffset, e.tryLength,
                        e.handlerOffset, e.handlerLength, classTokenOrFilter = e.classTokenOrFilter ?? "0x00000000"
                    }).ToArray(),
                    compileCount = records.Count,
                    ilConsistent = consistent
                };
                File.WriteAllText(Path.Combine(tokenDir, "meta.json"),
                    JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

                indexRows[$"0x{token:X8}"] = new
                {
                    best.name,
                    declaringType = best.declName,
                    declaringNamespace = best.declNs,
                    ilSize = best.ilSize,
                    hasLocalsBlob = blob != null,
                    ehCount = ehByNumber.Count,
                    ilConsistent = consistent
                };
                written++;
                if (verbose)
                    Console.WriteLine($"[idx {written,6}/{target.Count}] 0x{token:X8} {best.declName}::{best.name} IL={best.ilSize} map={tokenMap.Count} hints={hints.Count}");
            }

            File.WriteAllText(Path.Combine(outputDirectory, "index.json"),
                JsonSerializer.Serialize(indexRows, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outputDirectory, "inconsistent.json"),
                JsonSerializer.Serialize(inconsistentRows, new JsonSerializerOptions { WriteIndented = true }));
            var stats = new
            {
                methodDirsScanned = counters.dirs,
                targetScope,
                targetNamespace,
                targetMethods = written,
                inconsistentMethods = inconsistent,
                methodsWithTokenMap,
                tokenMapEntries,
                methodsWithLocalsBlob,
                methodsWithEh,
                globalHandleMappings = globalReal.Count,
                globalHandleConflicts = globalConflicts.Count,
                noIdentity = counters.noIdentity,
                dynamicOnly = counters.dynamic,
                placeholder = counters.placeholder,
                corpora = corpora.ToArray(),
                createdAt = DateTimeOffset.Now
            };
            File.WriteAllText(Path.Combine(outputDirectory, "stats.json"),
                JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[+] corpus merge complete: methods={written} tokenmaps={methodsWithTokenMap} entries={tokenMapEntries} scope={targetScope}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("index failed: " + ex);
            return 1;
        }
    }

}
