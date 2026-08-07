// DNGuardRebuilder v0.8.6-directproducerclosure (based on v0.7.3-localfix / v0.7.2) — splice runtime-captured IL back into the dumped module.
//
// UNIFIED PIPELINE (recommended) — index + rebuild in ONE process, rich per-method logging (Pipeline.cs):
//   DNGuardRebuilder --corpus <rc-dir...> --module <dump.dll> --out <rebuilt.dll>
//                    [--target-ns NS] [--only-types A B] [--prologue-mode strip] [--eh-mode flatten]
//                    [--resolve-refs on|off] [--dep-dir <publish dir>] [--verbose] [--quiet]
//
// LEGACY (pre-indexed) — rebuild from a by-token index produced by index_corpus.py:
//   DNGuardRebuilder --module <dump.dll> --index <index dir with by-token\> --out <rebuilt.dll>
//                    [--prologue-mode off|report|strip] [--eh-mode skip|flatten]
//                    [--resolve-refs on|off] [--dep-dir <publish dir>] [--dump-token <hex,hex>]
//
// v0.8 EXTERNAL-REF RESOLVER: unmapped virtual MemberRef/Field tokens (real token never leaked at the
//   JIT boundary — e.g. BCL/DevExpress calls DNGuard virtualized everywhere) are resolved against the
//   dump module's OWN metadata (rows survive DNGuard; only IL operands are virtualized) using hints.json
//   (declType+member+extToken from index_corpus) + an AssemblyResolver over the publish dir, then
//   Importer.Import -> Instruction.Operand fixup. Overloads disambiguate by the exact def token (extToken).
//   v0.8.1: generic type/method members (need a TypeSpec/MethodSpec the hint lacks).
//   v0.8.2 (adversarial review): (a) classify MethodSpec 0x2B as virtual — DNGuard virtualizes generic
//   CALLS (LINQ etc.); previously left raw => silent invalid-token corruption uncounted. (b) generic
//   members now resolve to the OPEN definition (readable HashSet<T>::Contains, un-truncated for reading)
//   flagged APPROX so metrics don't count them as exact. Exact instantiations still come via tokenmap.
//
// For each MethodDef in the index (target module only), replaces the DNGuard 0xB-byte stub body with
// the captured generated IL (real tokens), rebuilding locals, EH clauses and maxStack.
//
// v0.6 adds:
//   * ANTI-TAMPER PROLOGUE STRIP: every protected method starts with a fixed guard
//       ldsflda ZYXDNGuarder::a (virtual field) ; constrained. <virtual type> ; callvirt GetHashCode
//       (virtual method) ; call <junk> ...            then the real body.
//     We detect that guard on the RAW IL and (in --prologue-mode strip) overwrite the guard bytes with
//     `nop`. This is OFFSET-PRESERVING, so no branch/EH fix-up is needed and the unmapped virtual
//     class token (source of `unmapped`) disappears, letting the C# decompiler render cleanly.
//     Default mode is `report`: the guard is only detected + logged (per-method P) so the exact byte
//     pattern can be calibrated against real corpus (analyze_prologue.py) before mass-stripping.
//   * LOCALS RECONSTRUCTION (v0.3h): methods whose locals only came through methodSignature (cbSig=0,
//     no raw LocalVarSig blob) now use by-token\<hex>\locals-rebuilt.json (ordered CorInfoType +
//     pinned + module-valid type token/name) produced by index_corpus.py v6.1. This unlocks
//     Program.Main and the other 58 previously-skipped `locals-methodSig` methods.
//
// NOTE: dnlib API pinned to 4.x. Writing uses MetadataFlags.PreserveAll so all existing tokens/heaps
// stay valid (our IL references real module tokens — they must not be renumbered).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

public partial class Program {
    static string Arg(string[] a, string k, string def=null){
        for(int i=0;i<a.Length-1;i++) if(string.Equals(a[i],k,StringComparison.OrdinalIgnoreCase)) return a[i+1];
        return def;
    }

    enum PrologueMode { Off, Report, Strip }

    class Meta {
        public string token, name; public int ilSize, maxStack, ehCount;
        public bool hasLocalsBlob, dynamicOnly;
        public LocalsMeta locals; public List<EhMeta> eh = new();
    }
    class LocalsMeta { public int count; public int cbSig; }
    class EhMeta { public int ehNumber, flags, tryOffset, tryLength, handlerOffset, handlerLength; public string classTokenOrFilter; }
    class LocalDesc { public int corType; public bool pinned; public string token, ns, name; }

    static int Main(string[] args){
        // debug: --scan-strings <dll> — chi liet ke accessor CheckString (test port scan, khong rebuild).
        if(Arg(args,"--scan-strings")!=null) return ScanStringsDebug(Arg(args,"--scan-strings"));
        // v1.2 DRAG & DROP: exactly one non-flag existing-file arg = a protected exe dropped onto the exe.
        // Detect its dump under .\Dumps\ and run the full pipeline, then pause so the window stays open.
        if(args.Length==1 && !args[0].StartsWith("--") && File.Exists(args[0])) return RunDragDrop(args[0]);
        // v1.1 ALL-IN-ONE: --all runs capture (launch protected app under shim + ForceJit, auto-exit) THEN
        // index + rebuild — end to end, one command.
        if(HasFlag(args,"--all")) return RunAll(args);
        // v1.0 UNIFIED PIPELINE: --corpus <dir...> runs indexing (C# port of index_corpus.py) + rebuild in
        // ONE process with rich per-method logging. The legacy --index <dir> path (below) is unchanged.
        if(ArgList(args,"--corpus").Count>0) return RunPipeline(args);

        string modulePath = Arg(args,"--module");
        string indexDir   = Arg(args,"--index");
        string outPath    = Arg(args,"--out");
        string modeStr    = (Arg(args,"--prologue-mode","report") ?? "report").ToLowerInvariant();
        PrologueMode pmode = modeStr=="strip" ? PrologueMode.Strip : modeStr=="off" ? PrologueMode.Off : PrologueMode.Report;
        // --eh-mode skip|flatten (default skip). flatten: rebuild eh-missing methods with EH removed
        // (leave->br, endfinally/endfilter->nop/pop) so the REAL IL loads + decompiles for inspection.
        bool ehFlatten = string.Equals(Arg(args,"--eh-mode","skip"),"flatten",StringComparison.OrdinalIgnoreCase);
        // v0.8: EXTERNAL-REF RESOLVER — resolve unmapped virtual MemberRef/Field tokens against the dump
        // module's own metadata (rows survive DNGuard; only IL operands are virtualized), using an
        // AssemblyResolver over the publish dir so overloads disambiguate by the exact def signature.
        bool resolveRefs = !string.Equals(Arg(args,"--resolve-refs","on"),"off",StringComparison.OrdinalIgnoreCase);
        string fieldMode = (Arg(args,"--retarget-object-fields","high-confidence") ?? "high-confidence").ToLowerInvariant();
        bool retargetObjectFields = fieldMode!="off";
        string depDir = Arg(args,"--dep-dir");
        bool verbose = Array.Exists(args, x => string.Equals(x,"--verbose",StringComparison.OrdinalIgnoreCase));
        if(modulePath==null||indexDir==null||outPath==null){
            Console.Error.WriteLine("usage: DNGuardRebuilder --module <dll> --index <indexDir> --out <dll> [--prologue-mode off|report|strip] [--eh-mode skip|flatten] [--resolve-refs on|off] [--retarget-object-fields off|high-confidence] [--dep-dir <dir>]");
            return 2;
        }
        if(depDir==null) depDir = Path.GetDirectoryName(Path.GetFullPath(modulePath));
        string byToken = Path.Combine(indexDir,"by-token");
        if(!Directory.Exists(byToken)){ Console.Error.WriteLine("by-token not found: "+byToken); return 2; }

        Console.WriteLine("[*] loading module: "+modulePath);
        Console.WriteLine("[*] prologue-mode: "+pmode+"  resolve-refs: "+(resolveRefs?"on":"off")+"  retarget-fields: "+fieldMode);
        ModuleDefMD module;
        try {
            // AssemblyResolver over the publish dir so TypeRef.Resolve() finds BCL/DevExpress/etc.
            var modCtx = ModuleDef.CreateModuleContext();
            if(resolveRefs && modCtx.AssemblyResolver is AssemblyResolver ar){
                ar.EnableTypeDefCache = true;
                ar.UseGAC = false;                       // only trust the publish dir, not machine GAC
                ar.PreSearchPaths.Insert(0, depDir);
            }
            module = ModuleDefMD.Load(modulePath, modCtx);
        }
        catch(Exception ex){ Console.Error.WriteLine("dnlib load failed: "+ex.Message); return 3; }
        Console.WriteLine($"    MVID={module.Mvid}  name={module.Name}");
        if(resolveRefs) Console.WriteLine("    dep-dir (assembly resolver): "+depDir);

        // v0.8 external-ref resolver state (shared across methods; import cache dedups MemberRef rows).
        var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);
        var importCache = new Dictionary<string,IMemberRef>();
        var typeCache = new Dictionary<string,TypeDef>();
        int refsResolved=0, refsUnresolved=0, refsMethodsFixed=0, refsFieldsFixed=0, refsApprox=0;
        int refsInferred=0, refsInferFallback=0;
        var refsUnresolvedNames = new Dictionary<string,int>();
        var unresolvedByOpcode = new Dictionary<string,int>();

        int rebuilt=0; var skip = new Dictionary<string,int>();
        var rebuiltToks = new List<uint>();
        var rebuiltList = new List<string>();     // "token\tunmapped\tname" for rebuilt.txt
        var inMem = new Dictionary<uint,int>();   // token -> in-memory instruction count (before write)
        var localVerify = new Dictionary<uint,List<int>>(); // explicit raw local-index sequence
        int totPatched=0, totUnmapped=0;          // virtual-token translation stats
        int prologueDetected=0, prologueStripped=0, prologueBytesNopped=0, prologueSkippedEh=0, localsRebuiltUsed=0, localsInferred=0, ehFlattened=0;
        int localOperandsRebound=0, localOperandMethods=0, rawLocalRecovered=0, initobjFixed=0, deadInvalidNopped=0;
        int localTypesRefined=0, localTypeMethods=0, booleanLocalsRefined=0, parsedDeadNopped=0;
        int residualGenericLocalsFixed=0;
        int objectTypeOperandsRefined=0, objectTypeOperandMethods=0;
        int postFieldLocalRefined=0, postFieldObjectRefined=0, postFieldInitobjFixed=0;
        FieldRetargetResult fieldRetargetResult=null;
        var objectTypeRefinedByOpcode = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        var ehFlattenedToks = new HashSet<uint>();
        void Skip(string why){ skip[why]=skip.GetValueOrDefault(why)+1; }
        bool isMixed = !module.IsILOnly;
        Console.WriteLine($"    IsILOnly={module.IsILOnly}  (mixed-mode={isMixed})");

        foreach(var dir in Directory.EnumerateDirectories(byToken)){
            string hex = Path.GetFileName(dir);
            uint tok;
            if(!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out tok)){ Skip("bad-token-dir"); continue; }
            string ilPath = Path.Combine(dir,"il.bin");
            string metaPath = Path.Combine(dir,"meta.json");
            if(!File.Exists(ilPath)||!File.Exists(metaPath)){ Skip("no-il-or-meta"); continue; }

            Meta meta;
            try { meta = ParseMeta(File.ReadAllText(metaPath)); }
            catch { Skip("meta-parse"); continue; }

            // EH clauses expected but none captured. Default: skip (keep stub). --eh-mode flatten:
            // rebuild anyway with EH flattened so the real IL is at least inspectable in dnSpy.
            bool ehMissing = meta.ehCount>0 && meta.eh.Count==0;
            if(ehMissing && !ehFlatten){ Skip("eh-missing"); continue; }
            // v0.3h: locals via methodSignature are OK IF the indexer reconstructed them (locals-rebuilt.json).
            // v0.7: DNGuard exposes those locals only through an opaque methodSignature (no arg-list, so
            // getArgType can't read them). We INFER local types offline from IL usage instead — the count
            // is known exactly (locals.json), so the method still rebuilds + decompiles.
            string lrPath = Path.Combine(dir,"locals-rebuilt.json");
            bool hasRebuiltLocals = File.Exists(lrPath);

            var method = module.ResolveToken(tok) as MethodDef;
            if(method==null){ Skip("resolve-failed"); continue; }
            if(method.IsAbstract||method.IsPinvokeImpl){ Skip("abstract-or-pinvoke"); continue; }

            try {
                byte[] il = File.ReadAllBytes(ilPath);

                // v0.6: detect the DNGuard anti-tamper prologue on the RAW IL. In `strip` mode overwrite
                // it with nops (offset-preserving); in `report` mode only log it for calibration.
                if(pmode!=PrologueMode.Off && TryDetectPrologue(il, out int pEnd, out string pWhy)){
                    prologueDetected++;
                    // SAFETY: never nop a region that an EH clause reaches into (would corrupt the handler).
                    int minEh=int.MaxValue;
                    foreach(var e in meta.eh) minEh=Math.Min(minEh, Math.Min(e.tryOffset, e.handlerOffset));
                    bool ehInside = meta.eh.Count>0 && minEh<pEnd;
                    if(pmode==PrologueMode.Strip && !ehInside){
                        for(int k=0;k<pEnd;k++) il[k]=0x00;   // nop-fill up to the branch target (stack-safe)
                        prologueStripped++; prologueBytesNopped+=pEnd;
                    } else if(pmode==PrologueMode.Strip && ehInside){
                        prologueSkippedEh++;
                    } else if(prologueDetected<=20){
                        Console.WriteLine($"    [prologue] 0x{tok:X8} {meta.name}: guard=[0,0x{pEnd:X}) ({pEnd}B) body@0x{pEnd:X} {pWhy}");
                    }
                }

                // v0.5: translate DNGuard virtual tokens -> real module tokens before parsing.
                var vmap = LoadTokenmap(Path.Combine(dir,"tokenmap.json"));
                il = PatchTokens(il, vmap, out int tp, out int tu);
                totPatched += tp; totUnmapped += tu;
                int deadNop=NopDeadInvalidTokenInstructions(il);
                deadInvalidNopped+=deadNop;
                var gp = GenericParamContext.Create(method);
                IList<Parameter> pars = new List<Parameter>(method.Parameters);
                // il.bin is RAW IL (no method-body header). Explicit-header overload: codeSize=il.Length.
                CilBody body = MethodBodyReader.CreateCilBody(
                    module, il, (byte[])null, pars,
                    /*flags*/ (ushort)0, /*maxStack*/ (ushort)Math.Max(meta.maxStack, 8),
                    /*codeSize*/ (uint)il.Length, /*localVarSigTok*/ 0u, gp);
                if(body.Instructions.Count==0){ Skip("parsed-empty"); continue; }

                // v0.8: EXTERNAL-REF FIXUP — the still-virtual (unmapped) tokens left in `il` parse to
                // instructions with a null/invalid operand. Resolve each via hints.json (identity) against
                // the module + assembly resolver, then set the Instruction.Operand to the imported member.
                // Done at the operand level (not raw bytes) so freshly-imported MemberRefs — which have no
                // numeric token until write — attach correctly and serialize under PreserveAll.
                int mResolved=0;
                if(resolveRefs){
                    var hints = LoadHints(Path.Combine(dir,"hints.json"));
                    var byOff = new Dictionary<uint,Instruction>();
                    foreach(var ins in body.Instructions) byOff[ins.Offset]=ins;
                    foreach(var (o,vt) in CollectVirtualOperands(il)){
                        byOff.TryGetValue((uint)o, out var ins);
                        IMemberRef mem=null; bool isField=false, approx=false;
                        if(hints.TryGetValue(vt, out var hh))
                            mem = ResolveRef(module, importer, importCache, typeCache, hh, out isField, out approx);
                        if(mem!=null && ins!=null){
                            ins.Operand = mem; refsResolved++; mResolved++;
                            if(isField) refsFieldsFixed++; else refsMethodsFixed++;
                            if(approx) refsApprox++;
                        } else {
                            // still unresolved: tally by OPCODE (general residual profile, any sample) + name.
                            refsUnresolved++;
                            string opn = ins!=null ? ins.OpCode.Name : "?";
                            unresolvedByOpcode[opn]=unresolvedByOpcode.GetValueOrDefault(opn)+1;
                            if(hints.TryGetValue(vt, out var h2)){
                                string nm=$"{h2.ns}.{h2.type}::{h2.member}";
                                refsUnresolvedNames[nm]=refsUnresolvedNames.GetValueOrDefault(nm)+1;
                            } else {
                                refsUnresolvedNames[$"(no-hint {opn})"]=refsUnresolvedNames.GetValueOrDefault($"(no-hint {opn})")+1;
                            }
                        }
                    }
                }

                // ---- locals (v0.7.3: preserve numeric local operands from raw IL) -------------------
                string localsBinPath=Path.Combine(dir,"locals.bin");
                if(!PrepareLocalsAndRebind(module,body,pars,gp,meta,il,
                    File.Exists(localsBinPath)?localsBinPath:null,
                    hasRebuiltLocals?lrPath:null,
                    out string localMode,out int rebound,out int rawRecovered,out string localError)){
                    Skip(localError??"locals-prepare"); continue;
                }
                if(rebound>0){
                    localOperandsRebound+=rebound; localOperandMethods++;
                    var rawSeq=CaptureRawLocalIndices(il,out _).OrderBy(x=>x.Key).Select(x=>x.Value).ToList();
                    localVerify[tok]=rawSeq;
                }
                rawLocalRecovered+=rawRecovered;
                if(localMode=="rebuilt" && body.Variables.Count>0) localsRebuiltUsed++;
                if(localMode=="inferred" && body.Variables.Count>0) localsInferred++;

                // v0.7.5: refine weak/object locals even when locals-rebuilt.json exists. The old
                // index is still authoritative for COUNT/order, but System.Object / open !0 entries are
                // placeholders and can be strengthened from exact IL producers + consumers.
                int lref=RefineWeakLocalTypes(module,body,method,pars,out int bref);
                if(lref>0){ localTypesRefined+=lref; localTypeMethods++; booleanLocalsRefined+=bref; }

                // Exact managed-pointer evidence is stronger than an object fallback/tokenmap guess.
                initobjFixed+=RepairInitobjFromAddressProducer(body);

                // ---- EH — map IL offsets to parsed instructions ------------------------------------
                if(meta.eh.Count>0){
                    var byOff = new Dictionary<uint,Instruction>();
                    foreach(var ins in body.Instructions) byOff[ins.Offset]=ins;
                    Instruction At(long off){ return byOff.TryGetValue((uint)off, out var i)?i:null; }
                    foreach(var c in meta.eh){
                        var eh = new ExceptionHandler((ExceptionHandlerType)c.flags);
                        eh.TryStart     = At(c.tryOffset);
                        eh.TryEnd       = At(c.tryOffset + c.tryLength);
                        eh.HandlerStart = At(c.handlerOffset);
                        eh.HandlerEnd   = At(c.handlerOffset + c.handlerLength);
                        uint cf = ParseHex(c.classTokenOrFilter);
                        if((c.flags & 0x1)!=0)            // FILTER (cf = filter IL offset, NOT a token)
                            eh.FilterStart = At(cf);
                        else if(c.flags==0 && cf!=0){     // typed CATCH (cf = type token, may be virtual)
                            if(IsVirtual(cf) && vmap.TryGetValue(cf, out uint realcf)) cf=realcf;
                            var ct = module.ResolveToken(cf) as ITypeDefOrRef;
                            // v0.8.1: unmapped virtual catch-type resolves to null -> would emit an invalid
                            // typed-catch clause. Fall back to System.Object so the handler stays loadable.
                            eh.CatchType = ct ?? module.CorLibTypes.Object.ToTypeDefOrRef();
                        }
                        body.ExceptionHandlers.Add(eh);
                    }
                }

                // v0.7.2: EH-flatten (inspection) for methods whose EH clauses weren't captured.
                if(ehMissing && ehFlatten){ FlattenEH(body); ehFlattened++; ehFlattenedToks.Add(tok); }

                // v0.9: TYPE-OPERAND INFERENCE — type tokens (newarr/castclass/box/ldelem/stelem/...) are
                // never captured (DNGuard resolves them off the hooked JIT path), so they arrive as null
                // operands. Infer each from IL context (forward abstract stack: producer + consumer flow),
                // fall back to System.Object so the method never truncates. Runs after operands+locals are
                // set so it can read resolved method/field/local types. Best-effort (marked approx).
                if(resolveRefs){
                    int inf = InferTypeOperands(module, body, method, out int infFallback);
                    refsInferred += inf; refsInferFallback += infFallback;
                    // these were counted in refsUnresolved above; move the inferred ones out of that bucket
                    refsUnresolved -= inf; mResolved += inf;
                }

                // A second pass sees type operands imported/inferred above and can close additional
                // enumerator/current/handler/Boolean locals. Changing Local.Type preserves operand binding.
                int lref2=RefineWeakLocalTypes(module,body,method,pars,out int bref2);
                if(lref2>0){ localTypesRefined+=lref2; localTypeMethods++; booleanLocalsRefined+=bref2; }

                // v0.7.8: a final consensus pass for any !0/!!0 local that survived the normal
                // producer/consumer voting. It only commits a concrete non-object type when exact
                // producer/use evidence agrees, so valid generic methods/types are not altered.
                int rgfix=RepairResidualOpenGenericLocals(module,body,method,pars);
                if(rgfix>0){ residualGenericLocalsFixed+=rgfix; localTypesRefined+=rgfix; localTypeMethods++; }
                initobjFixed+=RepairInitobjFromAddressProducer(body);

                // v0.8.1: the first type-token pass runs before the final local strengthening. Re-run a
                // conservative object-flow solver now that local signatures are stronger. It may
                // replace System.Object for array/address operands plus exact box/constrained receivers
                // when exact producer/consumer evidence yields one concrete non-object type.
                int objectFixed=RefineObjectFlowOperands(module,body,method,out var objectByOp);
                if(objectFixed>0){
                    objectTypeOperandsRefined+=objectFixed; objectTypeOperandMethods++;
                    foreach(var kv in objectByOp) objectTypeRefinedByOpcode[kv.Key]=objectTypeRefinedByOpcode.GetValueOrDefault(kv.Key)+kv.Value;
                    int lref3=RefineWeakLocalTypes(module,body,method,pars,out int bref3);
                    if(lref3>0){ localTypesRefined+=lref3; localTypeMethods++; booleanLocalsRefined+=bref3; }
                    initobjFixed+=RepairInitobjFromAddressProducer(body);
                }

                // MethodBodyReader serializes a null method/field operand as 0xFFFFFFFF. Remove it only
                // when the instruction is provably dead: immediately skipped by an unconditional branch,
                // has no incoming branch/EH edge, and still has no metadata operand after all resolvers.
                int pdn=NopDeadUnresolvedInstructions(body);
                if(pdn>0){
                    parsedDeadNopped+=pdn; deadInvalidNopped+=pdn;
                    mResolved+=pdn;
                    refsUnresolved=Math.Max(0,refsUnresolved-pdn);
                }

                body.MaxStack = (ushort)Math.Max(meta.maxStack, 1);
                // Let dnlib RECOMPUTE maxStack on write (captured value may be unreliable).
                body.InitLocals = true;
                method.Body = body;
                method.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
                inMem[tok] = body.Instructions.Count;
                rebuilt++; rebuiltToks.Add(tok);
                // effective unmapped = raw-unmapped minus what the external-ref resolver fixed up.
                rebuiltList.Add($"0x{tok:X8}\t{Math.Max(0,tu-mResolved)}\t{(meta.name??"")}");
                if(verbose) Console.WriteLine($"[method {rebuilt,6}] 0x{tok:X8} {(meta.name??""),-42} IL={meta.ilSize,5} patch={tp,4} ref={mResolved,4} unmap={Math.Max(0,tu-mResolved),3} {(Math.Max(0,tu-mResolved)==0?"OK":"~")}");
            }
            catch(Exception ex){ Skip("rebuild-exception:"+ex.GetType().Name); }
        }

        // v0.8.0: field signatures are global metadata, so collect consensus only after every
        // rebuilt body has been installed. Retarget high-confidence System.Object fields, then rerun
        // local + array/address refinement over rebuilt methods with the stronger field signatures.
        int postFieldMethodsChanged=0;
        if(retargetObjectFields){
            string fieldReportDir=Path.GetDirectoryName(Path.GetFullPath(outPath));
            fieldRetargetResult=RetargetObjectFieldsHighConfidence(module,fieldReportDir);
            if(fieldRetargetResult.Retargeted>0){
                postFieldMethodsChanged=RefineAfterFieldRetarget(module,rebuiltToks,
                    ref postFieldLocalRefined,ref postFieldObjectRefined,ref postFieldInitobjFixed,
                    objectTypeRefinedByOpcode);
                localTypesRefined+=postFieldLocalRefined;
                objectTypeOperandsRefined+=postFieldObjectRefined;
                initobjFixed+=postFieldInitobjFixed;
            }
        }

        Console.WriteLine($"[*] rebuilt {rebuilt} methods; virtual tokens patched={totPatched}, unmapped={totUnmapped}");
        if(resolveRefs){
            Console.WriteLine($"[*] external-ref resolver: fixed={refsResolved} (methods={refsMethodsFixed}, fields={refsFieldsFixed}; of which approx-generic={refsApprox}); still-unresolved={refsUnresolved}");
            Console.WriteLine($"[*] type-operand inference (v0.9): inferred={refsInferred} (of which object-fallback={refsInferFallback})");
            Console.WriteLine("    still-unresolved BY OPCODE: "+string.Join("  ", unresolvedByOpcode.OrderByDescending(k=>k.Value).Select(k=>$"{k.Key}={k.Value}")));
            foreach(var kv in refsUnresolvedNames.OrderByDescending(k=>k.Value).Take(15))
                Console.WriteLine($"      unresolved {kv.Value,5}x  {kv.Key}");
        }
        Console.WriteLine($"[*] prologue: detected={prologueDetected} stripped={prologueStripped} skippedEH={prologueSkippedEh} bytesNopped={prologueBytesNopped}  (mode={pmode})");
        Console.WriteLine($"[*] locals reconstructed (v0.3h) used on {localsRebuiltUsed} methods; inferred (v0.7) on {localsInferred} methods");
        Console.WriteLine($"[*] local operands rebound (v0.7.3): {localOperandsRebound} across {localOperandMethods} methods; raw-count recovered={rawLocalRecovered}");
        Console.WriteLine($"[*] semantic local refinement (v0.8.6): {localTypesRefined} changes across {localTypeMethods} pass-events; Boolean={booleanLocalsRefined}");
        Console.WriteLine($"[*] residual open-generic locals closed (v0.8.6): {residualGenericLocalsFixed}");
        if(fieldRetargetResult!=null){
            Console.WriteLine($"[*] high-confidence object fields retargeted (v0.8.6): {fieldRetargetResult.Retargeted} (arrays={fieldRetargetResult.ArrayRetargeted}, evidence={fieldRetargetResult.FieldsWithEvidence}, conflicts={fieldRetargetResult.Conflicted}, weak-only={fieldRetargetResult.WeakOnly})");
            Console.WriteLine($"    post-field refinement: methods={postFieldMethodsChanged} locals={postFieldLocalRefined} type-operands={postFieldObjectRefined} initobj={postFieldInitobjFixed}");
            Console.WriteLine("    reports: field-retargets.jsonl, field-retarget-summary.json");
        }
        Console.WriteLine($"[*] object flow operands refined (v0.8.6): {objectTypeOperandsRefined} across {objectTypeOperandMethods} initial-pass methods");
        if(objectTypeRefinedByOpcode.Count>0)
            Console.WriteLine("    refined BY OPCODE: "+string.Join("  ",objectTypeRefinedByOpcode.OrderByDescending(k=>k.Value).Select(k=>$"{k.Key}={k.Value}")));
        Console.WriteLine($"[*] exact initobj repairs (v0.8.6): {initobjFixed}; dead invalid-token instructions nopped={deadInvalidNopped} (post-parse={parsedDeadNopped})");
        if(ehFlatten) Console.WriteLine($"[*] EH-flattened (v0.7.2, inspection-only) on {ehFlattened} methods");
        Console.WriteLine("[*] skipped:");
        foreach(var kv in skip.OrderByDescending(k=>k.Value)) Console.WriteLine($"      {kv.Value,6}  {kv.Key}");

        // rebuilt.txt next to the output: token, unmapped-count, name (unmapped=0 => fully translated)
        try {
            var lines = new List<string>{ "# token\tunmapped\tname  (unmapped=0 = token-translated only; see semantic-validation.txt)" };
            lines.AddRange(rebuiltList.OrderBy(s => { var p=s.Split('\t'); return (p.Length>1 && int.TryParse(p[1],out var u))?u:9999; }));
            string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            File.WriteAllLines(Path.Combine(outDir,"rebuilt.txt"), lines);
            int fully = rebuiltList.Count(s => { var p=s.Split('\t'); return p.Length>1 && p[1]=="0"; });
            Console.WriteLine("[*] wrote rebuilt.txt ("+rebuiltList.Count+" methods; fully-token-translated="+fully+")");
        } catch(Exception ex){ Console.WriteLine("    rebuilt.txt failed: "+ex.Message); }

        Console.WriteLine("[*] writing: "+outPath);
        try {
            var opts = new ModuleWriterOptions(module);
            opts.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
            opts.Logger = DummyLogger.NoThrowInstance;
            module.Write(outPath, opts);
        } catch(Exception ex){ Console.Error.WriteLine("write failed: "+ex.Message); return 4; }

        // SELF-VERIFY: reload the output and inspect a few REBUILT methods.
        Console.WriteLine("[*] self-verify (reloading output):");
        try {
            var outMod = ModuleDefMD.Load(outPath);
            int shown=0;
            foreach(var t in rebuiltToks){
                if(shown++>=10) break;
                var mm = outMod.ResolveToken(t) as MethodDef;
                var b = mm?.Body;
                int n = b?.Instructions?.Count ?? -1;
                string first = (b!=null && b.Instructions.Count>0)
                    ? b.Instructions[0].OpCode.Name + (b.Instructions[0].Operand is string s? " \""+s+"\"":"")
                    : "(none)";
                int nl = b?.Variables?.Count ?? -1;
                Console.WriteLine($"    {t:X8} {mm?.Name}: inMem={inMem.GetValueOrDefault(t,-1)} reloaded={n} locals={nl} first=[{first}]");
            }
            Console.WriteLine("    (if 'first' is ldstr \"Error, DNGuard...\", the body was NOT replaced on write)");
            int lvChecked=0,lvBad=0,lvMethodsBad=0;
            foreach(var kv in localVerify){
                var mm=outMod.ResolveToken(kv.Key) as MethodDef;
                var actual=new List<int>();
                if(mm?.Body!=null){
                    foreach(var ins in mm.Body.Instructions)
                        if(IsExplicitLocalOperand(ins.OpCode.Code)) actual.Add(LocalIndex(ins));
                }
                int bad=0,ncmp=Math.Max(kv.Value.Count,actual.Count);
                for(int i=0;i<ncmp;i++){
                    int e=i<kv.Value.Count?kv.Value[i]:-2;
                    int a=i<actual.Count?actual[i]:-3;
                    if(e!=a) bad++;
                }
                lvChecked+=kv.Value.Count; lvBad+=bad;
                if(bad>0){
                    lvMethodsBad++;
                    if(lvMethodsBad<=20)
                        Console.WriteLine($"    [local-verify BAD] 0x{kv.Key:X8} {mm?.Name}: expected={kv.Value.Count} actual={actual.Count} mismatches={bad}");
                }
            }
            Console.WriteLine($"    [local-verify] operands={lvChecked} mismatches={lvBad} methods-bad={lvMethodsBad}");
        } catch(Exception ex){ Console.WriteLine("    self-verify failed: "+ex.Message); }

        try {
            var validation=ValidateWrittenModule(outPath,rebuiltToks,ehFlattenedToks);
            Console.WriteLine("[*] post-write semantic validator (v0.8.6):");
            Console.WriteLine($"    methods={validation.Methods} rebuilt={validation.RebuiltMethods} structurally-valid={validation.StructurallyValid}");
            Console.WriteLine($"    semantic-core-clean={validation.SemanticCoreClean} semantic-strict-clean={validation.SemanticStrictClean} issue-methods={validation.IssueMethods}");
            Console.WriteLine($"    invalid-token={validation.InvalidMetadataOperands} stubs={validation.DnGuardStubs} open-generic-locals={validation.OpenGenericLocals} initobj-mismatch={validation.InitobjMismatches}");
            Console.WriteLine($"    bad-locals={validation.BadLocalOperands} bad-branches={validation.BadBranchTargets} object-operands={validation.ObjectTypeOperands} proven-object={validation.ProvenObjectOperands} object-fallbacks={validation.ObjectFallbackOperands}");
            Console.WriteLine("    reports: semantic-validation.txt, semantic-validation.json, semantic-findings.jsonl, semantic-operands.jsonl, semantic-object-proven.jsonl, field-retargets.jsonl");
        } catch(Exception ex){ Console.WriteLine("    semantic validator failed: "+ex.Message); }

        // --dump-token <hex>[,<hex>...] : reload output and print full IL (opcode + operand) of each
        // method, so external-ref substitutions can be verified against the real member they now point to.
        string dumpArg = Arg(args,"--dump-token");
        if(!string.IsNullOrEmpty(dumpArg)){
            try {
                var outMod = ModuleDefMD.Load(outPath);
                foreach(var part in dumpArg.Split(',')){
                    uint dt = ParseHex(part.Trim()); if(dt==0) continue;
                    var mm = outMod.ResolveToken(dt) as MethodDef;
                    Console.WriteLine($"\n===== DUMP 0x{dt:X8} {mm?.DeclaringType?.Name}::{mm?.Name} =====");
                    var b = mm?.Body; if(b==null){ Console.WriteLine("  (no body)"); continue; }
                    foreach(var ins in b.Instructions){
                        string opnd="";
                        if(ins.Operand is IMethod im) opnd=$"{im.FullName}  [{(im.MDToken.Raw!=0?("0x"+im.MDToken.Raw.ToString("X8")):"imported")}]";
                        else if(ins.Operand is IField f) opnd=$"{f.DeclaringType?.FullName}::{f.Name}";
                        else if(ins.Operand is ITypeDefOrRef ty) opnd=ty.FullName;
                        else if(ins.Operand is string s) opnd="\""+s+"\"";
                        else if(ins.Operand!=null) opnd=ins.Operand.ToString();
                        Console.WriteLine($"  IL_{ins.Offset:X4}  {ins.OpCode.Name,-12} {opnd}");
                    }
                }
            } catch(Exception ex){ Console.WriteLine("  dump failed: "+ex.Message); }
        }

        Console.WriteLine("[*] done.");
        return 0;
    }

    // ==== v0.6 anti-tamper prologue detection ==================================================
    // A DNGuard guard prologue: (optional leading nops) ldsflda/ldsfld <virtual FIELD> ; ... ;
    // constrained. <virtual TYPE> ; callvirt <virtual METHOD> ; and trailing junk/virtual guard
    // instructions + short branches, up to the first real-body instruction. Returns the byte length
    // of the guard region [0,pEnd). Conservative: requires all three signature parts, else false.
    struct Ins { public int off, len; public byte op; public int fe2; public bool hasTok; public uint tok; }

    static List<Ins> Decode(byte[] c){
        var outp = new List<Ins>(); int i=0,n=c.Length;
        while(i<n){
            var ins = new Ins{ off=i, fe2=-1 };
            byte op=c[i++]; ins.op=op;
            if(op==0xFE){
                if(i>=n){ ins.len=i-ins.off; outp.Add(ins); break; }
                byte o2=c[i++]; ins.fe2=o2;
                if(o2==0x06||o2==0x07||o2==0x15||o2==0x16||o2==0x1C){ // token-bearing FE
                    if(i+4>n){ ins.len=i-ins.off; outp.Add(ins); break; }
                    ins.hasTok=true; ins.tok=BitConverter.ToUInt32(c,i); i+=4;
                } else if(o2==0x09||o2==0x0A||o2==0x0B||o2==0x0C||o2==0x0D||o2==0x0E) i+=2;
                else if(o2==0x12||o2==0x19) i+=1;
                ins.len=i-ins.off; outp.Add(ins); continue;
            }
            int L=SingleOperandLen(op);
            if(L==-2){ if(i+4>n){ ins.len=i-ins.off; outp.Add(ins); break; } uint cnt=BitConverter.ToUInt32(c,i); i+=4+4*(int)cnt; }
            else if(TOK_OPS.Contains(op)){ if(i+4>n){ ins.len=i-ins.off; outp.Add(ins); break; } ins.hasTok=true; ins.tok=BitConverter.ToUInt32(c,i); i+=4; }
            else i+=L;
            ins.len=i-ins.off; outp.Add(ins);
        }
        return outp;
    }

    static bool IsJunkTok(uint t){
        if(IsVirtual(t)) return false;
        uint tbl=t>>24;
        // valid metadata tables that can legitimately appear as an IL operand
        return !(tbl==0x01||tbl==0x02||tbl==0x04||tbl==0x06||tbl==0x0A||tbl==0x11||tbl==0x1B||tbl==0x70||tbl==0x0B);
    }
    static bool IsShortOrLongBranch(byte op){ return (op>=0x2B&&op<=0x44); }        // br.s..blt.un / br..blt.un
    static bool IsLoadConst(byte op){ return (op>=0x15&&op<=0x20)||op==0x1F; }       // ldc.i4.m1..8 / ldc.i4.s / ldc.i4

    // Absolute target of a branch instruction at ins[k], or -1 if not a branch.
    static int ReadBranchTarget(List<Ins> ins, int k, byte[] c){
        var q=ins[k]; byte op=q.op; int next=q.off+q.len;
        if((op>=0x2B && op<=0x37) || op==0xDE) return next + (sbyte)c[q.off+1];              // short (incl. leave.s)
        if((op>=0x38 && op<=0x44) || op==0xDD) return next + BitConverter.ToInt32(c,q.off+1); // long  (incl. leave)
        return -1;
    }

    // DNGuard guard prologue: (opt nops) ldsflda/ldsfld <vfield 0x04800001> ; constrained. <vtype
    // 0x01800002> ; callvirt/call <vmethod 0x0A80xxxx = GetHashCode> ; then a tamper-check branch
    // (brtrue.s/br.s ...) whose TARGET is the real method body. pEnd = that branch target: everything
    // before it is the check (stack-neutral: ldsflda +1, callvirt GetHashCode net 0, brtrue -1 => 0)
    // plus the now-dead "not loaded" handler. A branch target is a basic-block boundary with stack
    // depth 0, so nop-filling [0,pEnd) is provably stack-safe. Needs the 3-part signature + a forward
    // branch. (Earlier greedy "consume trailing virtual/junk ops" over-ran real bodies that begin with
    // a token op, e.g. get_Settings' `ldsfld` — that left the guard region non-stack-neutral.)
    static bool TryDetectPrologue(byte[] c, out int pEnd, out string why){
        pEnd=0; why="";
        var ins=Decode(c);
        if(ins.Count==0){ why="empty"; return false; }
        int idx=0; while(idx<ins.Count && ins[idx].op==0x00) idx++;
        if(idx>=ins.Count){ why="all-nops"; return false; }
        var f=ins[idx];
        if(!((f.op==0x7F||f.op==0x7E) && f.hasTok && IsVirtual(f.tok) && (f.tok>>24)==0x04)){ why="no-ldsflda-vfield"; return false; }
        // signature: constrained.(vtype) then the FIRST callvirt/call (vmethod) = GetHashCode
        bool sawCT=false; int cv=-1; int lim=Math.Min(ins.Count, idx+8);
        for(int j=idx+1; j<lim; j++){
            var q=ins[j];
            if(q.op==0xFE && q.fe2==0x16 && q.hasTok && IsVirtual(q.tok) && (q.tok>>24)==0x01) sawCT=true;
            if((q.op==0x6F||q.op==0x28) && q.hasTok && IsVirtual(q.tok) && (q.tok>>24)==0x0A){ cv=j; break; }
        }
        if(!sawCT || cv<0){ why="incomplete-signature"; return false; }
        // leading branch cluster right after the callvirt: max forward branch target across nops.
        int cvEnd=ins[cv].off+ins[cv].len; int best=-1; bool sawBranch=false;
        for(int k=cv+1; k<ins.Count; k++){
            var q=ins[k];
            if(q.op==0x00) continue;                       // nop inside the cluster
            int t=ReadBranchTarget(ins,k,c);
            if(t>=0){ sawBranch=true; if(t>best) best=t; continue; }
            break;                                          // first productive instruction => cluster ends
        }
        if(!sawBranch){ why="no-tamper-branch"; return false; }
        if(best<=cvEnd || best>=c.Length){ why=$"branch-target-out-of-range(0x{best:X})"; return false; }
        pEnd=best; why="ok";
        return true;
    }

    // ==== v0.3h locals reconstruction ==========================================================
    static TypeSig Prim(ModuleDefMD m, int ct){
        var c=m.CorLibTypes;
        switch(ct){
            case 0x01: return c.Void;   case 0x02: return c.Boolean; case 0x03: return c.Char;
            case 0x04: return c.SByte;  case 0x05: return c.Byte;    case 0x06: return c.Int16;  case 0x07: return c.UInt16;
            case 0x08: return c.Int32;  case 0x09: return c.UInt32;  case 0x0a: return c.Int64;  case 0x0b: return c.UInt64;
            case 0x0c: return c.IntPtr; case 0x0d: return c.UIntPtr; case 0x0e: return c.Single; case 0x0f: return c.Double;
            case 0x10: return c.String; case 0x15: return c.TypedReference;
            default:   return null;
        }
    }
    static ITypeDefOrRef FindType(ModuleDefMD m, string ns, string name){
        if(string.IsNullOrEmpty(name)) return null;
        ns = ns ?? "";
        foreach(var t in m.GetTypes())    if(t.Name==name && (t.Namespace??"")==ns) return t;
        foreach(var tr in m.GetTypeRefs()) if(tr.Name==name && (tr.Namespace??"")==ns) return tr;
        return null;
    }
    // Returns the local's TypeSig. ok=false => cannot safely fake this local (size-sensitive) => skip method.
    static TypeSig BuildLocalTypeSig(ModuleDefMD module, LocalDesc d, out bool ok){
        ok=true; int ct=d.corType;
        var prim=Prim(module,ct); if(prim!=null) return prim;      // primitives + string + typedref
        ITypeDefOrRef tr=null;
        if(!string.IsNullOrEmpty(d.token)){ uint tk=ParseHex(d.token); tr=module.ResolveToken(tk) as ITypeDefOrRef; }
        if(tr==null && !string.IsNullOrEmpty(d.name)) tr=FindType(module, d.ns, d.name);
        if(tr is TypeSpec ts2) return ts2.TypeSig ?? module.CorLibTypes.Object;
        switch(ct){
            case 0x14: // CLASS (ref) — safe to fall back to object (same slot size)
                return tr!=null ? (TypeSig)new ClassSig(tr) : module.CorLibTypes.Object;
            case 0x13: // VALUECLASS — wrong type = wrong frame size; must resolve or skip
                if(tr!=null) return new ValueTypeSig(tr);
                ok=false; return null;
            case 0x11: // PTR
                return new PtrSig(tr!=null ? tr.ToTypeSig() : module.CorLibTypes.Void);
            case 0x12: // BYREF
                if(tr!=null) return new ByRefSig(tr.ToTypeSig());
                ok=false; return null;
            default:
                return module.CorLibTypes.Object;   // unknown ref-ish -> object
        }
    }
    static List<LocalDesc> LoadLocalsRebuilt(string path){
        var list=new List<LocalDesc>();
        if(!File.Exists(path)) return list;
        try{
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            if(doc.RootElement.TryGetProperty("locals", out var arr) && arr.ValueKind==JsonValueKind.Array){
                foreach(var e in arr.EnumerateArray()){
                    var d=new LocalDesc();
                    if(e.TryGetProperty("corType",out var ct)) d.corType=ct.GetInt32();
                    if(e.TryGetProperty("pinned",out var pn) && (pn.ValueKind==JsonValueKind.True||pn.ValueKind==JsonValueKind.False)) d.pinned=pn.GetBoolean();
                    if(e.TryGetProperty("token",out var tk)) d.token=tk.GetString();
                    if(e.TryGetProperty("ns",out var ns))    d.ns=ns.GetString();
                    if(e.TryGetProperty("name",out var nm))  d.name=nm.GetString();
                    list.Add(d);
                }
            }
        }catch{}
        return list;
    }

    // ==== v0.7.3 raw-local preservation + exact address-type repair ==============================
    // MethodBodyReader parses raw IL before a LocalVarSig exists. For explicit local opcodes
    // (ldloc.s/ldloca.s/stloc.s and inline-var forms), dnlib can bind every operand to a dummy Local
    // with Index=0. Capture the numeric indices from the raw IL first, create the real Local list,
    // then rebind every explicit local operand by instruction offset.
    static Dictionary<uint,int> CaptureRawLocalIndices(byte[] c, out int maxIndex){
        var map=new Dictionary<uint,int>(); maxIndex=-1;
        int i=0,n=c?.Length??0;
        while(i<n){
            int start=i; byte op=c[i++];
            if(op>=0x06 && op<=0x09){ maxIndex=Math.Max(maxIndex, op-0x06); continue; }
            if(op>=0x0A && op<=0x0D){ maxIndex=Math.Max(maxIndex, op-0x0A); continue; }
            if(op==0xFE){
                if(i>=n) break; byte o2=c[i++];
                if(o2==0x0C||o2==0x0D||o2==0x0E){
                    if(i+2>n) break;
                    int idx=BitConverter.ToUInt16(c,i); map[(uint)start]=idx;
                    maxIndex=Math.Max(maxIndex,idx); i+=2; continue;
                }
                if(o2==0x06||o2==0x07||o2==0x15||o2==0x16||o2==0x1C){ i+=Math.Min(4,n-i); continue; }
                if(o2==0x09||o2==0x0A||o2==0x0B){ i+=Math.Min(2,n-i); continue; }
                if(o2==0x12||o2==0x19){ i+=Math.Min(1,n-i); continue; }
                continue;
            }
            if(op==0x11||op==0x12||op==0x13){
                if(i>=n) break; int idx=c[i++]; map[(uint)start]=idx;
                maxIndex=Math.Max(maxIndex,idx); continue;
            }
            int L=SingleOperandLen(op);
            if(L==-2){
                if(i+4>n) break; uint cnt=BitConverter.ToUInt32(c,i);
                i=(int)Math.Min(n,(long)i+4L+4L*cnt);
            } else i=Math.Min(n,i+Math.Max(L,0));
        }
        return map;
    }

    static bool IsExplicitLocalOperand(Code c)=>
        c==Code.Ldloc||c==Code.Ldloc_S||c==Code.Ldloca||c==Code.Ldloca_S||
        c==Code.Stloc||c==Code.Stloc_S;

    static int RebindLocalOperands(CilBody body, Dictionary<uint,int> raw, out int missing){
        int rebound=0; missing=0;
        foreach(var ins in body.Instructions){
            if(!IsExplicitLocalOperand(ins.OpCode.Code)) continue;
            if(!raw.TryGetValue(ins.Offset,out int idx)){ missing++; continue; }
            if(idx<0||idx>=body.Variables.Count){ missing++; continue; }
            ins.Operand=body.Variables[idx]; rebound++;
        }
        return rebound;
    }

    static bool PrepareLocalsAndRebind(ModuleDefMD module, CilBody body, IList<Parameter> pars,
        GenericParamContext gp, Meta meta, byte[] rawIl, string localsBinPath, string localsRebuiltPath,
        out string mode, out int rebound, out int rawRecovered, out string error){

        mode="none"; rebound=0; rawRecovered=0; error=null;
        var raw=CaptureRawLocalIndices(rawIl,out int rawMax);
        int declared=meta?.locals?.count ?? 0;
        var exactTypes=new List<TypeSig>(); bool exact=false;

        if(meta!=null && meta.hasLocalsBlob && !string.IsNullOrEmpty(localsBinPath) && File.Exists(localsBinPath)){
            try {
                var sig=SignatureReader.ReadSig(module,File.ReadAllBytes(localsBinPath),gp) as LocalSig;
                if(sig==null){ error="locals-blob-invalid"; return false; }
                exactTypes.AddRange(sig.Locals); exact=true; mode="blob";
            } catch(Exception ex){ error="locals-blob:"+ex.GetType().Name; return false; }
        } else if(!string.IsNullOrEmpty(localsRebuiltPath) && File.Exists(localsRebuiltPath)){
            try {
                foreach(var d in LoadLocalsRebuilt(localsRebuiltPath)){
                    TypeSig ts=BuildLocalTypeSig(module,d,out bool tokOk);
                    if(!tokOk||ts==null){ error="locals-rebuilt-unresolved"; return false; }
                    if(d.pinned) ts=new PinnedSig(ts);
                    exactTypes.Add(ts);
                }
                exact=true; mode="rebuilt";
            } catch(Exception ex){ error="locals-rebuilt:"+ex.GetType().Name; return false; }
        }

        int count=exact ? exactTypes.Count : Math.Max(declared,rawMax+1);
        if(rawMax>=count){
            if(exact){ error=$"local-index-out-of-range:{rawMax}/{count}"; return false; }
            count=rawMax+1;
        }
        rawRecovered=Math.Max(0,count-declared);

        body.Variables.Clear();
        if(exact) foreach(var t in exactTypes) body.Variables.Add(new Local(t));
        else {
            mode=count>0?"inferred":"none";
            for(int i=0;i<count;i++) body.Variables.Add(new Local(module.CorLibTypes.Object));
        }

        rebound=RebindLocalOperands(body,raw,out int missing);
        if(missing>0){ error="local-rebind-missing:"+missing; return false; }

        if(!exact && count>0){
            var inferred=InferLocals(module,body,pars,count);
            body.Variables.Clear();
            for(int i=0;i<count;i++)
                body.Variables.Add(new Local(i<inferred.Count ? inferred[i] : module.CorLibTypes.Object));
            rebound=RebindLocalOperands(body,raw,out missing);
            if(missing>0){ error="local-rebind-after-infer:"+missing; return false; }
        }
        return true;
    }

    static Instruction PrevReal(IList<Instruction> ins,int k){
        for(int j=k-1;j>=0;j--) if(ins[j].OpCode.Code!=Code.Nop) return ins[j];
        return null;
    }

    static TypeSig AddressReferentType(Instruction p){
        if(p==null) return null;
        switch(p.OpCode.Code){
            case Code.Ldflda: case Code.Ldsflda: {
                var f=p.Operand as IField;
                return CloseDeclaringTypeArgs(f?.FieldSig?.Type,f?.DeclaringType); }
            case Code.Ldloca: case Code.Ldloca_S:
                return (p.Operand as Local)?.Type;
            case Code.Ldarga: case Code.Ldarga_S:
                return (p.Operand as Parameter)?.Type;
            case Code.Ldelema:
                return (p.Operand as ITypeDefOrRef)?.ToTypeSig();
            default: return null;
        }
    }

    static int RepairInitobjFromAddressProducer(CilBody body){
        int fixedCount=0; var ins=body.Instructions;
        for(int k=0;k<ins.Count;k++){
            var cur=ins[k]; if(cur.OpCode.Code!=Code.Initobj) continue;
            TypeSig t=AddressReferentType(PrevReal(ins,k));
            if(t==null) continue;
            var tdr=t.ToTypeDefOrRef(); if(tdr==null) continue;
            string old=(cur.Operand as ITypeDefOrRef)?.FullName;
            if(!string.Equals(old,tdr.FullName,StringComparison.Ordinal)){
                cur.Operand=tdr; fixedCount++;
            }
        }
        return fixedCount;
    }

    class RawIlInst {
        public int Offset,End,OperandOffset,OperandLength;
        public byte Op,Op2;
        public bool Token,Unconditional;
        public List<int> Targets=new();
    }

    static List<RawIlInst> ReadRawIl(byte[] c){
        var list=new List<RawIlInst>(); int i=0,n=c?.Length??0;
        while(i<n){
            int start=i; byte op=c[i++],o2=0; int operand=i,L=0; bool token=false,uncond=false;
            var targets=new List<int>();
            if(op==0xFE){
                if(i>=n) break; o2=c[i++]; operand=i;
                if(o2==0x06||o2==0x07||o2==0x15||o2==0x16||o2==0x1C){L=4;token=true;}
                else if(o2==0x09||o2==0x0A||o2==0x0B||o2==0x0C||o2==0x0D||o2==0x0E)L=2;
                else if(o2==0x12||o2==0x19)L=1;
            } else if(op==0x45){
                if(i+4>n) break; uint cnt=BitConverter.ToUInt32(c,i); L=4+4*(int)cnt;
                int baseOff=i+L;
                for(int x=0;x<cnt && i+4+4*x+4<=n;x++)
                    targets.Add(baseOff+BitConverter.ToInt32(c,i+4+4*x));
            } else {
                L=SingleOperandLen(op); if(L<0)L=0; token=TOK_OPS.Contains(op);
                if((op>=0x2B&&op<=0x37)||op==0xDE){
                    if(i<n) targets.Add(i+1+(sbyte)c[i]);
                    uncond=op==0x2B||op==0xDE;
                } else if((op>=0x38&&op<=0x44)||op==0xDD){
                    if(i+4<=n) targets.Add(i+4+BitConverter.ToInt32(c,i));
                    uncond=op==0x38||op==0xDD;
                }
            }
            int end=Math.Min(n,i+L);
            list.Add(new RawIlInst{Offset=start,End=end,OperandOffset=operand,OperandLength=L,
                Op=op,Op2=o2,Token=token,Unconditional=uncond,Targets=targets});
            i=end;
        }
        return list;
    }

    static int NopDeadInvalidTokenInstructions(byte[] c){
        var all=ReadRawIl(c); var targets=new HashSet<int>();
        foreach(var x in all) foreach(int t in x.Targets) targets.Add(t);
        int count=0;
        for(int k=1;k<all.Count;k++){
            var x=all[k]; if(!x.Token||x.OperandLength!=4||x.OperandOffset+4>c.Length) continue;
            if(BitConverter.ToUInt32(c,x.OperandOffset)!=0xFFFFFFFFu) continue;
            var p=all[k-1];
            bool jumpsOver=p.Unconditional && p.Targets.Any(t=>t>=x.End);
            bool targeted=targets.Any(t=>t>=x.Offset&&t<x.End);
            if(!jumpsOver||targeted) continue;
            for(int j=x.Offset;j<x.End;j++) c[j]=0x00;
            count++;
        }
        return count;
    }

    static bool IsUnconditionalBranch(Code c)=>
        c==Code.Br||c==Code.Br_S||c==Code.Leave||c==Code.Leave_S;

    static bool IsMetadataOperandInstruction(Instruction i){
        if(i==null) return false;
        switch(i.OpCode.OperandType){
            case OperandType.InlineMethod:
            case OperandType.InlineField:
            case OperandType.InlineType:
            case OperandType.InlineTok:
            case OperandType.InlineString:
            case OperandType.InlineSig:
                return true;
            default: return false;
        }
    }

    static bool IsMissingMetadataOperand(Instruction i){
        if(!IsMetadataOperandInstruction(i)) return false;
        if(i.Operand==null) return true;
        if(i.Operand is uint u) return u==0xFFFFFFFFu;
        if(i.Operand is int si) return unchecked((uint)si)==0xFFFFFFFFu;
        if(i.Operand is IMDTokenProvider mdp) return mdp.MDToken.Raw==0xFFFFFFFFu;
        return false;
    }

    static int NopDeadUnresolvedInstructions(CilBody body){
        if(body==null) return 0;
        var ins=body.Instructions;
        var incoming=new HashSet<Instruction>();
        foreach(var x in ins){
            if(x.Operand is Instruction t) incoming.Add(t);
            else if(x.Operand is Instruction[] aa) foreach(var q in aa) if(q!=null) incoming.Add(q);
        }
        foreach(var eh in body.ExceptionHandlers){
            if(eh.TryStart!=null) incoming.Add(eh.TryStart);
            if(eh.TryEnd!=null) incoming.Add(eh.TryEnd);
            if(eh.HandlerStart!=null) incoming.Add(eh.HandlerStart);
            if(eh.HandlerEnd!=null) incoming.Add(eh.HandlerEnd);
            if(eh.FilterStart!=null) incoming.Add(eh.FilterStart);
        }

        int count=0;
        for(int k=1;k<ins.Count;k++){
            var cur=ins[k]; if(!IsMissingMetadataOperand(cur)||incoming.Contains(cur)) continue;
            var prev=PrevReal(ins,k);
            if(prev==null||!IsUnconditionalBranch(prev.OpCode.Code)||prev.Operand is not Instruction target)
                continue;
            int targetIndex=ins.IndexOf(target);
            if(targetIndex<=k) continue; // must jump forward over this unresolved instruction
            cur.OpCode=OpCodes.Nop; cur.Operand=null; count++;
        }
        return count;
    }

    class SemanticValidationSummary {
        public int Methods, RebuiltMethods, StructurallyValid, SemanticCoreClean, SemanticStrictClean;
        public int InvalidMetadataOperands, DnGuardStubs, OpenGenericLocals, InitobjMismatches;
        public int BadLocalOperands, BadBranchTargets, ObjectTypeOperands;
        public int ProvenObjectOperands, ObjectFallbackOperands, EhFlattenedMethods;
        public int ObjectFieldDefinitions, ObjectArrayFieldDefinitions;
        public int IssueMethods;
        public string ObjectProvenancePolicy="non-circular-direct-producer-v0.8.6";
        public Dictionary<string,int> ProvenObjectByOpcode = new();
        public Dictionary<string,int> ObjectFallbackByOpcode = new();
    }

    static bool IsDnGuardStubBody(CilBody body){
        if(body==null) return false;
        var live=body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToList();
        if(live.Count<3||live[0].OpCode.Code!=Code.Ldstr) return false;
        string text=live[0].Operand as string;
        return text!=null&&text.IndexOf("DNGuard Runtime library not loaded",
            StringComparison.OrdinalIgnoreCase)>=0&&live.Any(i=>i.OpCode.Code==Code.Throw);
    }

    static int PreviousRealIndex(IList<Instruction> instructions,int index){
        for(int j=index-1;j>=0;j--)
            if(instructions[j].OpCode.Code!=Code.Nop) return j;
        return -1;
    }
    static int FollowingRealIndex(IList<Instruction> instructions,int index){
        for(int j=index+1;j<instructions.Count;j++)
            if(instructions[j].OpCode.Code!=Code.Nop) return j;
        return -1;
    }
    static bool IsExactObjectArray(TypeSig type){
        type=StripByRef(type);
        if(type is SZArraySig sz) return IsNamed(sz.Next,"System.Object");
        if(type is ArraySig ar) return IsNamed(ar.Next,"System.Object");
        return false;
    }

    static int ParameterIndex(Instruction instruction){
        if(instruction==null) return -1;
        switch(instruction.OpCode.Code){
            case Code.Ldarg_0: return 0;
            case Code.Ldarg_1: return 1;
            case Code.Ldarg_2: return 2;
            case Code.Ldarg_3: return 3;
            case Code.Ldarg: case Code.Ldarg_S:
                if(instruction.Operand is Parameter parameter) return (int)parameter.Index;
                return -1;
            default:
                return -1;
        }
    }

    // v0.8.6: build a stack-aware authoritative array graph and retain direct producer closure.
    //
    // The v0.8.3 graph recognized only an immediately adjacent producer/sink and the final explicit
    // call argument. That misses the dominant C# shape for params/object arrays:
    //
    //     newarr object; dup; ...; stelem.ref; ...; call M(..., objectArray, ...)
    //
    // and it also misses locals passed as any argument other than the final one. This graph follows
    // array values through the real evaluation stack and local copies, but still accepts only
    // independent metadata as evidence: parameter/field/method-return types and call parameter types.
    // Local signatures and the Object operand currently under audit are never used as seeds.
    class ArrayTraceSlot {
        public int LocalIndex=-1;
        public TypeSig AuthoritativeType;
        public Instruction Origin;
    }
    class AuthoritativeArrayEvidence {
        public Dictionary<int,TypeSig> LocalTypes=new Dictionary<int,TypeSig>();
        public Dictionary<Instruction,TypeSig> OriginTypes=new Dictionary<Instruction,TypeSig>();
        // Element instructions need the authoritative array receiver, not merely a nearby local.
        // This map is resolved after local/origin voting so a direct `newarr; dup; ...; ldelema`
        // chain can inherit the metadata sink discovered later in the same method.
        public Dictionary<Instruction,TypeSig> ReceiverTypes=new Dictionary<Instruction,TypeSig>();
        public int LocalConflicts, OriginConflicts, ReceiverConflicts;
    }
    static ArrayTraceSlot ArrayPeek(List<ArrayTraceSlot> stack,int fromTop){
        int index=stack.Count-1-fromTop;
        return index>=0&&index<stack.Count?stack[index]:null;
    }

    static AuthoritativeArrayEvidence BuildAuthoritativeArrayEvidence(
        ModuleDefMD module, MethodDef method, CilBody body){
        var localVotes=new Dictionary<int,Dictionary<string,TypeSig>>();
        var originVotes=new Dictionary<Instruction,Dictionary<string,TypeSig>>();
        // Store receiver identities first. Their concrete type may only become known after a later
        // call/field/return sink votes the originating newarr or local.
        var receiverSlots=new Dictionary<Instruction,List<ArrayTraceSlot>>();
        var copyEdges=new List<(int from,int to)>();
        var copyEdgeKeys=new HashSet<string>(StringComparer.Ordinal);
        var assignmentCount=new Dictionary<int,int>();
        var instructions=body.Instructions;

        bool IsArray(TypeSig candidate){
            candidate=StripByRef(candidate);
            return candidate is SZArraySig||candidate is ArraySig;
        }
        void VoteLocal(int localIndex,TypeSig candidate){
            candidate=StripByRef(candidate);
            if(localIndex<0||localIndex>=body.Variables.Count||!IsArray(candidate)) return;
            string key=TypeKey(candidate);
            if(string.IsNullOrEmpty(key)) return;
            if(!localVotes.TryGetValue(localIndex,out var bucket))
                localVotes[localIndex]=bucket=new Dictionary<string,TypeSig>(StringComparer.Ordinal);
            bucket[key]=candidate;
        }
        void VoteOrigin(Instruction origin,TypeSig candidate){
            candidate=StripByRef(candidate);
            if(origin==null||origin.OpCode.Code!=Code.Newarr||!IsArray(candidate)) return;
            string key=TypeKey(candidate);
            if(string.IsNullOrEmpty(key)) return;
            if(!originVotes.TryGetValue(origin,out var bucket))
                originVotes[origin]=bucket=new Dictionary<string,TypeSig>(StringComparer.Ordinal);
            bucket[key]=candidate;
        }
        void VoteSlot(ArrayTraceSlot slot,TypeSig candidate){
            candidate=StripByRef(candidate);
            if(slot==null||!IsArray(candidate)) return;
            if(slot.LocalIndex>=0) VoteLocal(slot.LocalIndex,candidate);
            if(slot.Origin!=null) VoteOrigin(slot.Origin,candidate);
        }
        void AddCopy(int from,int to){
            if(from<0||to<0||from==to) return;
            string key=from.ToString()+">"+to.ToString();
            if(copyEdgeKeys.Add(key)) copyEdges.Add((from,to));
        }
        void RecordReceiver(Instruction consumer,ArrayTraceSlot slot){
            if(consumer==null||slot==null) return;
            if(!receiverSlots.TryGetValue(consumer,out var list))
                receiverSlots[consumer]=list=new List<ArrayTraceSlot>();
            list.Add(new ArrayTraceSlot{
                LocalIndex=slot.LocalIndex,
                AuthoritativeType=slot.AuthoritativeType,
                Origin=slot.Origin
            });
        }

        TypeSig AuthoritativeProducer(Instruction ins){
            if(ins==null) return null;
            switch(ins.OpCode.Code){
                case Code.Ldarg: case Code.Ldarg_S: case Code.Ldarg_0:
                case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3: {
                    int pi=ParameterIndex(ins);
                    if(pi>=0&&pi<method.Parameters.Count)
                        return StripByRef(method.Parameters[pi].Type);
                    return null;
                }
                case Code.Ldfld: case Code.Ldsfld: {
                    var field=ins.Operand as IField;
                    return StripByRef(CloseDeclaringTypeArgs(field?.FieldSig?.Type,field?.DeclaringType));
                }
                case Code.Call: case Code.Callvirt: {
                    var called=ins.Operand as IMethod;
                    return StripByRef(CloseMethodTypeArgs(called?.MethodSig?.RetType,called));
                }
                case Code.Castclass: case Code.Isinst:
                    return StripByRef((ins.Operand as ITypeDefOrRef)?.ToTypeSig());
                default:
                    return null;
            }
        }

        // Count assignments and preserve cheap adjacent copy/source evidence. This provides a
        // conservative fallback when a branch target clears the abstract stack.
        for(int k=0;k<instructions.Count;k++){
            var ins=instructions[k];
            if(!IsStloc(ins.OpCode.Code)) continue;
            int dst=LocalIndex(ins);
            assignmentCount[dst]=assignmentCount.GetValueOrDefault(dst)+1;
            int previous=PreviousRealIndex(instructions,k);
            if(previous<0) continue;
            var producer=instructions[previous];
            if(IsLdloc(producer.OpCode.Code)) AddCopy(LocalIndex(producer),dst);
            else VoteLocal(dst,AuthoritativeProducer(producer));
        }

        var branchTargets=new HashSet<Instruction>();
        foreach(var ins in instructions){
            if(ins.Operand is Instruction one) branchTargets.Add(one);
            else if(ins.Operand is IList<Instruction> many)
                foreach(var target in many) if(target!=null) branchTargets.Add(target);
        }

        var stack=new List<ArrayTraceSlot>();
        var pars=new List<Parameter>(method.Parameters);
        for(int k=0;k<instructions.Count;k++){
            var ins=instructions[k];
            var code=ins.OpCode.Code;
            if(branchTargets.Contains(ins)) stack.Clear();

            // Consumers are inspected before their operands are popped.
            // Preserve the exact array receiver for typed element operations. A later sink may
            // prove the origin/local as Object[] or a concrete T[] after this instruction was seen.
            if(code==Code.Ldelem||code==Code.Ldelema)
                RecordReceiver(ins,ArrayPeek(stack,1));
            else if(code==Code.Stelem)
                RecordReceiver(ins,ArrayPeek(stack,2));

            if(IsStloc(code)){
                int dst=LocalIndex(ins);
                var value=ArrayPeek(stack,0);
                if(value!=null){
                    VoteLocal(dst,value.AuthoritativeType);
                    if(value.LocalIndex>=0) AddCopy(value.LocalIndex,dst);
                }
            }
            else if(code==Code.Starg||code==Code.Starg_S){
                var value=ArrayPeek(stack,0);
                int pi=ins.Operand is Parameter parameter?(int)parameter.Index:-1;
                if(pi>=0&&pi<method.Parameters.Count) VoteSlot(value,method.Parameters[pi].Type);
            }
            else if(code==Code.Stfld||code==Code.Stsfld){
                var field=ins.Operand as IField;
                TypeSig target=StripByRef(CloseDeclaringTypeArgs(field?.FieldSig?.Type,field?.DeclaringType));
                VoteSlot(ArrayPeek(stack,0),target);
            }
            else if(code==Code.Ret){
                VoteSlot(ArrayPeek(stack,0),StripByRef(method.MethodSig?.RetType));
            }
            else if(code==Code.Call||code==Code.Callvirt||code==Code.Newobj){
                var called=ins.Operand as IMethod;
                var sig=called?.MethodSig;
                if(sig!=null){
                    int parameterCount=sig.Params.Count;
                    for(int a=0;a<parameterCount;a++){
                        var argument=ArrayPeek(stack,parameterCount-1-a);
                        TypeSig parameterType=StripByRef(CloseMethodTypeArgs(sig.Params[a],called));
                        VoteSlot(argument,parameterType);
                    }
                }
            }

            if(code==Code.Dup){
                var top=ArrayPeek(stack,0);
                stack.Add(new ArrayTraceSlot{
                    LocalIndex=top?.LocalIndex??-1,
                    AuthoritativeType=top?.AuthoritativeType,
                    Origin=top?.Origin
                });
                continue;
            }

            ArrayTraceSlot carried=null;
            if(code==Code.Castclass||code==Code.Isinst){
                var top=ArrayPeek(stack,0);
                if(top!=null) carried=new ArrayTraceSlot{
                    LocalIndex=top.LocalIndex,AuthoritativeType=top.AuthoritativeType,Origin=top.Origin
                };
            }

            int pop=PopCount(ins);
            if(pop>=999) stack.Clear();
            else for(int n=0;n<pop&&stack.Count>0;n++) stack.RemoveAt(stack.Count-1);

            if(IsLdloc(code)){
                stack.Add(new ArrayTraceSlot{LocalIndex=LocalIndex(ins)});
                continue;
            }
            if(code==Code.Newarr){
                TypeSig element=(ins.Operand as ITypeDefOrRef)?.ToTypeSig();
                TypeSig arrayType=!IsNamed(element,"System.Object")?MakeSzArray(element):null;
                stack.Add(new ArrayTraceSlot{AuthoritativeType=arrayType,Origin=ins});
                continue;
            }
            if(code==Code.Castclass||code==Code.Isinst){
                TypeSig castType=StripByRef((ins.Operand as ITypeDefOrRef)?.ToTypeSig());
                stack.Add(new ArrayTraceSlot{
                    LocalIndex=carried?.LocalIndex??-1,
                    Origin=carried?.Origin,
                    AuthoritativeType=IsArray(castType)?castType:carried?.AuthoritativeType
                });
                continue;
            }

            int push; TypeSig pushedType;
            PushInfo(module,ins,pars,module.CorLibTypes.Object,out push,out pushedType);
            pushedType=StripByRef(pushedType);
            bool metadataArrayProducer=
                code==Code.Ldarg||code==Code.Ldarg_S||code==Code.Ldarg_0||
                code==Code.Ldarg_1||code==Code.Ldarg_2||code==Code.Ldarg_3||
                code==Code.Ldfld||code==Code.Ldsfld||
                code==Code.Call||code==Code.Callvirt;
            for(int n=0;n<push;n++){
                stack.Add(new ArrayTraceSlot{
                    AuthoritativeType=metadataArrayProducer&&IsArray(pushedType)?pushedType:null
                });
            }

            if(code==Code.Br||code==Code.Br_S||code==Code.Leave||code==Code.Leave_S||
               code==Code.Ret||code==Code.Throw||code==Code.Rethrow)
                stack.Clear();
        }

        // Copy propagation is safe forward. Reverse propagation is allowed only when the destination
        // has exactly one assignment, so a metadata sink attached to the copied local can constrain
        // its source without merging unrelated values.
        for(int pass=0;pass<12;pass++){
            bool changed=false;
            foreach(var edge in copyEdges){
                if(localVotes.TryGetValue(edge.from,out var fromVotes)){
                    foreach(var candidate in fromVotes.Values.ToList()){
                        int before=localVotes.TryGetValue(edge.to,out var toBucket)?toBucket.Count:0;
                        VoteLocal(edge.to,candidate);
                        int after=localVotes.TryGetValue(edge.to,out toBucket)?toBucket.Count:0;
                        if(after>before) changed=true;
                    }
                }
                if(assignmentCount.GetValueOrDefault(edge.to)==1&&
                   localVotes.TryGetValue(edge.to,out var toVotes)){
                    foreach(var candidate in toVotes.Values.ToList()){
                        int before=localVotes.TryGetValue(edge.from,out var fromBucket)?fromBucket.Count:0;
                        VoteLocal(edge.from,candidate);
                        int after=localVotes.TryGetValue(edge.from,out fromBucket)?fromBucket.Count:0;
                        if(after>before) changed=true;
                    }
                }
            }
            if(!changed) break;
        }

        var result=new AuthoritativeArrayEvidence();
        foreach(var pair in localVotes){
            if(pair.Value.Count==1) result.LocalTypes[pair.Key]=pair.Value.Values.First();
            else if(pair.Value.Count>1) result.LocalConflicts++;
        }
        foreach(var pair in originVotes){
            if(pair.Value.Count==1) result.OriginTypes[pair.Key]=pair.Value.Values.First();
            else if(pair.Value.Count>1) result.OriginConflicts++;
        }
        foreach(var pair in receiverSlots){
            var candidates=new Dictionary<string,TypeSig>(StringComparer.Ordinal);
            foreach(var slot in pair.Value){
                TypeSig candidate=StripByRef(slot.AuthoritativeType);
                if(candidate==null&&slot.LocalIndex>=0)
                    result.LocalTypes.TryGetValue(slot.LocalIndex,out candidate);
                if(candidate==null&&slot.Origin!=null)
                    result.OriginTypes.TryGetValue(slot.Origin,out candidate);
                candidate=StripByRef(candidate);
                if(!IsArray(candidate)) continue;
                string key=TypeKey(candidate);
                if(!string.IsNullOrEmpty(key)) candidates[key]=candidate;
            }
            if(candidates.Count==1) result.ReceiverTypes[pair.Key]=candidates.Values.First();
            else if(candidates.Count>1) result.ReceiverConflicts++;
        }
        return result;
    }

    static Dictionary<int,TypeSig> BuildAuthoritativeArrayLocalTypes(
        ModuleDefMD module,MethodDef method,CilBody body){
        return BuildAuthoritativeArrayEvidence(module,method,body).LocalTypes;
    }

    static TypeSig FindNearbyAuthoritativeArrayLocal(
        CilBody body,int index,IReadOnlyDictionary<int,TypeSig> localTypes){
        var candidates=new Dictionary<string,TypeSig>(StringComparer.Ordinal);
        for(int j=index-1;j>=0&&j>=index-12;j--){
            var code=body.Instructions[j].OpCode.Code;
            if(code==Code.Br||code==Code.Br_S||code==Code.Leave||code==Code.Leave_S||
               code==Code.Ret||code==Code.Throw||code==Code.Rethrow||code==Code.Switch)
                break;
            if(!IsLdloc(code)) continue;
            int li=LocalIndex(body.Instructions[j]);
            if(li>=0&&localTypes.TryGetValue(li,out var candidate))
                candidates[TypeKey(candidate)]=candidate;
        }
        return candidates.Count==1?candidates.Values.First():null;
    }

    // Strengthen locals and Object array operands from the authoritative sink graph. The map can
    // prove Object[] exact without changing IL, or repair a concrete T[] when one metadata type wins.
    static int RefineAuthoritativeArraySinkOperands(
        ModuleDefMD module,MethodDef method,CilBody body,Dictionary<string,int> byOpcode){
        var evidence=BuildAuthoritativeArrayEvidence(module,method,body);
        var localArrays=evidence.LocalTypes;
        if(localArrays.Count==0&&evidence.OriginTypes.Count==0&&evidence.ReceiverTypes.Count==0) return 0;
        int changed=0;
        foreach(var pair in localArrays){
            int li=pair.Key; var target=StripByRef(pair.Value);
            if(li<0||li>=body.Variables.Count) continue;
            var current=StripByRef(body.Variables[li].Type);
            bool weak=IsNamed(current,"System.Object")||IsExactObjectArray(current);
            if(weak&&!string.Equals(TypeKey(current),TypeKey(target),StringComparison.Ordinal))
                body.Variables[li].Type=target;
        }

        bool SetOperand(Instruction ins,TypeSig element){
            element=StripByRef(element);
            if(ins==null||element==null||IsNamed(element,"System.Object")) return false;
            if(!IsSystemObjectTypeOperand(ins)) return false;
            var tdr=ToTypeOperandRef(element); if(tdr==null) return false;
            ins.Operand=tdr; changed++;
            string op=ins.OpCode.Name??ins.OpCode.Code.ToString();
            byOpcode[op]=byOpcode.GetValueOrDefault(op)+1;
            return true;
        }

        foreach(var pair in evidence.OriginTypes){
            var origin=pair.Key;
            var arrayType=StripByRef(pair.Value);
            if(origin==null||origin.OpCode.Code!=Code.Newarr) continue;
            SetOperand(origin,ElemOf(arrayType));
        }

        var instructions=body.Instructions;
        for(int k=0;k<instructions.Count;k++){
            var ins=instructions[k];
            if(ins.OpCode.Code==Code.Newarr&&IsSystemObjectTypeOperand(ins)){
                int next=FollowingRealIndex(instructions,k);
                if(next>=0&&IsStloc(instructions[next].OpCode.Code)){
                    int li=LocalIndex(instructions[next]);
                    if(li>=0&&localArrays.TryGetValue(li,out var arrayType)){
                        var element=ElemOf(arrayType);
                        SetOperand(ins,element);
                    }
                }
                continue;
            }
            if((ins.OpCode.Code==Code.Ldelem||ins.OpCode.Code==Code.Ldelema||
                ins.OpCode.Code==Code.Stelem)&&IsSystemObjectTypeOperand(ins)){
                TypeSig arrayType=null;
                evidence.ReceiverTypes.TryGetValue(ins,out arrayType);
                arrayType??=FindNearbyAuthoritativeArrayLocal(body,k,localArrays);
                var element=ElemOf(arrayType);
                SetOperand(ins,element);
            }
        }
        return changed;
    }
    // Validator evidence must be independent of the fallback being validated. Local signatures are
    // intentionally excluded here: many locals were inferred from the same `newarr/initobj Object`
    // instruction, so accepting them would create circular "proof".
    static TypeSig AuthoritativeValidationTargetType(MethodDef method,Instruction consumer){
        if(consumer==null) return null;
        switch(consumer.OpCode.Code){
            case Code.Stfld: case Code.Stsfld: {
                var field=consumer.Operand as IField;
                return CloseDeclaringTypeArgs(field?.FieldSig?.Type,field?.DeclaringType);
            }
            case Code.Ret:
                return method.MethodSig?.RetType;
            case Code.Call: case Code.Callvirt: case Code.Newobj: {
                var called=consumer.Operand as IMethod;
                var sig=called?.MethodSig;
                if(sig==null||sig.Params.Count==0) return null;
                return CloseMethodTypeArgs(sig.Params[sig.Params.Count-1],called);
            }
            default:
                return null;
        }
    }
    static TypeSig FindNearbyAuthoritativeArrayProducer(ModuleDefMD module,MethodDef method,CilBody body,int index){
        var instructions=body.Instructions;
        var known=body.Variables.Select(v=>v.Type).ToArray();
        var pars=new List<Parameter>(method.Parameters);
        var candidates=new Dictionary<string,TypeSig>(StringComparer.Ordinal);
        for(int j=index-1;j>=0&&j>=index-12;j--){
            var code=instructions[j].OpCode.Code;
            if(code==Code.Br||code==Code.Br_S||code==Code.Leave||code==Code.Leave_S||
               code==Code.Ret||code==Code.Throw||code==Code.Rethrow||code==Code.Switch)
                break;
            // Locals and newarr are deliberately excluded: their type may have been inferred from the
            // exact Object fallback under review. Parameters, fields and method returns are independent
            // metadata evidence.
            bool authoritative=code==Code.Ldarg||code==Code.Ldarg_S||code==Code.Ldarg_0||
                code==Code.Ldarg_1||code==Code.Ldarg_2||code==Code.Ldarg_3||
                code==Code.Ldfld||code==Code.Ldsfld||code==Code.Call||code==Code.Callvirt;
            if(!authoritative) continue;
            TypeSig candidate=StripByRef(ContextualProducedType(module,instructions,j,pars,known));
            if(candidate is SZArraySig||candidate is ArraySig)
                candidates[TypeKey(candidate)]=candidate;
        }
        return candidates.Count==1?candidates.Values.First():null;
    }
    static bool IsProvenExactObjectOperand(ModuleDefMD module,MethodDef method,CilBody body,int index,
        AuthoritativeArrayEvidence authoritativeArrays,out string reason){
        reason=null;
        var authoritativeArrayLocals=authoritativeArrays?.LocalTypes;
        if(index<0||index>=body.Instructions.Count) return false;
        var instruction=body.Instructions[index];
        switch(instruction.OpCode.Code){
            case Code.Newarr: {
                if(authoritativeArrays!=null&&
                   authoritativeArrays.OriginTypes.TryGetValue(instruction,out var originArray)&&
                   IsExactObjectArray(originArray)){
                    reason="authoritative-stack-sink-object-array"; return true;
                }
                int next=FollowingRealIndex(body.Instructions,index);
                TypeSig target=next>=0?AuthoritativeValidationTargetType(method,body.Instructions[next]):null;
                if(IsExactObjectArray(target)){ reason="authoritative-target-object-array"; return true; }
                if(next>=0&&IsStloc(body.Instructions[next].OpCode.Code)){
                    int li=LocalIndex(body.Instructions[next]);
                    if(li>=0&&authoritativeArrayLocals!=null&&
                       authoritativeArrayLocals.TryGetValue(li,out var localArray)&&IsExactObjectArray(localArray)){
                        reason="authoritative-local-sink-object-array"; return true;
                    }
                }
                return false;
            }
            case Code.Ldelem: case Code.Ldelema: case Code.Stelem: {
                if(authoritativeArrays!=null&&
                   authoritativeArrays.ReceiverTypes.TryGetValue(instruction,out var receiverArray)&&
                   IsExactObjectArray(receiverArray)){
                    reason="authoritative-stack-receiver-object-array"; return true;
                }
                TypeSig array=FindNearbyAuthoritativeArrayProducer(module,method,body,index);
                if(IsExactObjectArray(array)){ reason="receiver-object-array"; return true; }
                if(authoritativeArrayLocals!=null){
                    var localArray=FindNearbyAuthoritativeArrayLocal(body,index,authoritativeArrayLocals);
                    if(IsExactObjectArray(localArray)){
                        reason="authoritative-local-receiver-object-array"; return true;
                    }
                }
                return false;
            }
            case Code.Initobj: case Code.Ldobj: case Code.Constrained: {
                int previous=PreviousRealIndex(body.Instructions,index);
                if(previous<0) return false;
                var producer=body.Instructions[previous];
                // Only metadata-backed addresses are authoritative. `ldloca object` is excluded because
                // that local may itself have been inferred from this same fallback.
                if(producer.OpCode.Code!=Code.Ldflda&&producer.OpCode.Code!=Code.Ldsflda&&
                   producer.OpCode.Code!=Code.Ldarga&&producer.OpCode.Code!=Code.Ldarga_S)
                    return false;
                TypeSig referent=AddressReferentType(producer);
                if(IsNamed(StripByRef(referent),"System.Object")){
                    reason="metadata-object-address-referent"; return true;
                }
                return false;
            }
            default:
                return false;
        }
    }

    static SemanticValidationSummary ValidateWrittenModule(string outPath, ICollection<uint> rebuiltTokens,
        ICollection<uint> ehFlattenedTokens=null){
        var summary=new SemanticValidationSummary();
        var rebuilt=rebuiltTokens!=null?new HashSet<uint>(rebuiltTokens):new HashSet<uint>();
        var flattened=ehFlattenedTokens!=null?new HashSet<uint>(ehFlattenedTokens):new HashSet<uint>();
        string outDir=Path.GetDirectoryName(Path.GetFullPath(outPath));
        string jsonlPath=Path.Combine(outDir,"semantic-findings.jsonl");
        string operandJsonlPath=Path.Combine(outDir,"semantic-operands.jsonl");
        var jsonLines=new List<string>();
        var operandLines=new List<string>();
        var provenObjectLines=new List<string>();

        using var mod=ModuleDefMD.Load(outPath);
        foreach(var field in mod.GetTypes().SelectMany(t=>t.Fields)){
            var ft=field.FieldSig?.Type;
            if(IsNamed(ft,"System.Object")) summary.ObjectFieldDefinitions++;
            if(ft is SZArraySig sz&&IsNamed(sz.Next,"System.Object")) summary.ObjectArrayFieldDefinitions++;
        }
        foreach(var method in mod.GetTypes().SelectMany(t=>t.Methods)){
            var body=method.Body; if(body==null) continue;
            summary.Methods++;
            if(rebuilt.Contains(method.MDToken.Raw)) summary.RebuiltMethods++;
            bool wasFlattened=flattened.Contains(method.MDToken.Raw);
            if(wasFlattened) summary.EhFlattenedMethods++;

            int invalid=0,openGeneric=0,initMismatch=0,badLocal=0,badBranch=0;
            int objTotal=0,objProven=0,objFallback=0;
            bool stub=IsDnGuardStubBody(body);
            if(stub){
                summary.DnGuardStubs++;
                operandLines.Add(JsonSerializer.Serialize(new {
                    kind="dnguard-stub", token="0x"+method.MDToken.Raw.ToString("X8"),
                    type=method.DeclaringType?.FullName??"", method=method.Name?.ToString()??""
                }));
            }

            var instructionSet=new HashSet<Instruction>(body.Instructions);
            var authoritativeArrays=BuildAuthoritativeArrayEvidence(mod,method,body);
            bool genericContext=method.HasGenericParameters||
                (method.DeclaringType!=null&&method.DeclaringType.HasGenericParameters);

            for(int li=0;li<body.Variables.Count;li++){
                var local=body.Variables[li];
                if(!genericContext&&HasGenericVar(local.Type)){
                    openGeneric++;
                    operandLines.Add(JsonSerializer.Serialize(new {
                        kind="open-generic-local", token="0x"+method.MDToken.Raw.ToString("X8"),
                        type=method.DeclaringType?.FullName??"", method=method.Name?.ToString()??"",
                        localIndex=li, localType=local.Type?.FullName??""
                    }));
                }
            }

            for(int k=0;k<body.Instructions.Count;k++){
                var ins=body.Instructions[k];
                if(IsMissingMetadataOperand(ins)){
                    invalid++;
                    operandLines.Add(JsonSerializer.Serialize(new {
                        kind="invalid-metadata-operand", token="0x"+method.MDToken.Raw.ToString("X8"),
                        type=method.DeclaringType?.FullName??"", method=method.Name?.ToString()??"",
                        offset="IL_"+ins.Offset.ToString("X4"), opcode=ins.OpCode.Name
                    }));
                }

                if(IsExplicitLocalOperand(ins.OpCode.Code)){
                    int li=LocalIndex(ins);
                    if(li<0||li>=body.Variables.Count) badLocal++;
                }

                if(ins.Operand is Instruction target){
                    if(!instructionSet.Contains(target)) badBranch++;
                } else if(ins.Operand is Instruction[] targets){
                    foreach(var target2 in targets)
                        if(target2==null||!instructionSet.Contains(target2)) badBranch++;
                }

                if(NeedsTypeOp(ins.OpCode.Code)&&ins.Operand is ITypeDefOrRef typeOperand&&
                   string.Equals(typeOperand.FullName,"System.Object",StringComparison.Ordinal)){
                    objTotal++;
                    string op=ins.OpCode.Name??"?";
                    if(IsProvenExactObjectOperand(mod,method,body,k,authoritativeArrays,out string objectReason)){
                        objProven++;
                        summary.ProvenObjectByOpcode[op]=summary.ProvenObjectByOpcode.GetValueOrDefault(op)+1;
                        provenObjectLines.Add(JsonSerializer.Serialize(new {
                            kind="object-type-proven", token="0x"+method.MDToken.Raw.ToString("X8"),
                            type=method.DeclaringType?.FullName??"", method=method.Name?.ToString()??"",
                            offset="IL_"+ins.Offset.ToString("X4"), opcode=op, reason=objectReason
                        }));
                    } else {
                        objFallback++;
                        summary.ObjectFallbackByOpcode[op]=summary.ObjectFallbackByOpcode.GetValueOrDefault(op)+1;
                        operandLines.Add(JsonSerializer.Serialize(new {
                            kind="object-type-fallback", token="0x"+method.MDToken.Raw.ToString("X8"),
                            type=method.DeclaringType?.FullName??"", method=method.Name?.ToString()??"",
                            offset="IL_"+ins.Offset.ToString("X4"), opcode=op
                        }));
                    }
                }

                if(ins.OpCode.Code==Code.Initobj){
                    TypeSig expected=AddressReferentType(PrevReal(body.Instructions,k));
                    TypeSig actual=(ins.Operand as ITypeDefOrRef)?.ToTypeSig();
                    if(expected!=null&&actual!=null&&
                       !string.Equals(TypeKey(expected),TypeKey(actual),StringComparison.Ordinal)){
                        initMismatch++;
                        operandLines.Add(JsonSerializer.Serialize(new {
                            kind="initobj-mismatch", token="0x"+method.MDToken.Raw.ToString("X8"),
                            type=method.DeclaringType?.FullName??"", method=method.Name?.ToString()??"",
                            offset="IL_"+ins.Offset.ToString("X4"), opcode=ins.OpCode.Name,
                            expected=expected.FullName??"", actual=actual.FullName??""
                        }));
                    }
                }
            }

            summary.InvalidMetadataOperands+=invalid;
            summary.OpenGenericLocals+=openGeneric;
            summary.InitobjMismatches+=initMismatch;
            summary.BadLocalOperands+=badLocal;
            summary.BadBranchTargets+=badBranch;
            summary.ObjectTypeOperands+=objTotal;
            summary.ProvenObjectOperands+=objProven;
            summary.ObjectFallbackOperands+=objFallback;

            bool structural=invalid==0&&badLocal==0&&badBranch==0;
            bool core=structural&&!stub&&openGeneric==0&&initMismatch==0;
            bool strict=core&&objFallback==0;
            if(structural) summary.StructurallyValid++;
            if(core) summary.SemanticCoreClean++;
            if(strict) summary.SemanticStrictClean++;

            if(!core||objFallback>0){
                summary.IssueMethods++;
                var rec=new {
                    token="0x"+method.MDToken.Raw.ToString("X8"),
                    type=method.DeclaringType?.FullName??"",
                    method=method.Name?.ToString()??"",
                    rebuilt=rebuilt.Contains(method.MDToken.Raw),
                    invalidMetadataOperands=invalid,
                    dnGuardStub=stub,
                    openGenericLocals=openGeneric,
                    initobjMismatches=initMismatch,
                    badLocalOperands=badLocal,
                    badBranchTargets=badBranch,
                    objectTypeOperands=objTotal,
                    provenObjectOperands=objProven,
                    objectFallbackOperands=objFallback,
                    ehClauses=body.ExceptionHandlers.Count,
                    ehFlattened=wasFlattened
                };
                jsonLines.Add(JsonSerializer.Serialize(rec));
            }
        }

        var options=new JsonSerializerOptions{WriteIndented=true,IncludeFields=true};
        File.WriteAllText(Path.Combine(outDir,"semantic-validation.json"),
            JsonSerializer.Serialize(summary,options));
        File.WriteAllLines(jsonlPath,jsonLines);
        File.WriteAllLines(operandJsonlPath,operandLines);
        File.WriteAllLines(Path.Combine(outDir,"semantic-object-proven.jsonl"),provenObjectLines);
        var text=new List<string>{
            "DNGuardRebuilder v0.8.6 post-write semantic validation",
            "======================================================",
            $"Methods with body       : {summary.Methods}",
            $"Rebuilt methods         : {summary.RebuiltMethods}",
            $"Structurally valid      : {summary.StructurallyValid}",
            $"Semantic core clean     : {summary.SemanticCoreClean}",
            $"Semantic strict clean   : {summary.SemanticStrictClean}",
            $"Issue methods           : {summary.IssueMethods}",
            $"Invalid metadata operand: {summary.InvalidMetadataOperands}",
            $"DNGuard stubs           : {summary.DnGuardStubs}",
            $"Open generic locals     : {summary.OpenGenericLocals}",
            $"initobj mismatches      : {summary.InitobjMismatches}",
            $"Bad local operands      : {summary.BadLocalOperands}",
            $"Bad branch targets      : {summary.BadBranchTargets}",
            $"Object type operands   : {summary.ObjectTypeOperands}",
            $"Proven exact Object    : {summary.ProvenObjectOperands}",
            $"Object fallback operands: {summary.ObjectFallbackOperands}",
            $"Object FieldDefs       : {summary.ObjectFieldDefinitions}",
            $"Object[] FieldDefs     : {summary.ObjectArrayFieldDefinitions}",
            $"EH-flattened methods    : {summary.EhFlattenedMethods}",
            "",
            "Proven exact Object by opcode:",
        };
        foreach(var kv in summary.ProvenObjectByOpcode.OrderByDescending(k=>k.Value))
            text.Add($"  {kv.Key,-14} {kv.Value}");
        text.Add("");
        text.Add("Unproven Object fallbacks by opcode:");
        foreach(var kv in summary.ObjectFallbackByOpcode.OrderByDescending(k=>k.Value))
            text.Add($"  {kv.Key,-14} {kv.Value}");
        text.AddRange(new[]{
            "",
            "Semantic core clean excludes invalid operands, stubs, open generic locals,",
            "initobj mismatches, local-index errors and branch-target errors.",
            "Semantic strict clean additionally requires zero unproven System.Object type operands."
        });
        File.WriteAllLines(Path.Combine(outDir,"semantic-validation.txt"),text);
        return summary;
    }

    // ==== v0.7 offline local-type inference =====================================================
    // DNGuard's methodSignature-only locals can't be read via getArgType (no arg-list). But the exact
    // COUNT is known (locals.json), and the generated IL uses REAL tokens after PatchTokens, so dnlib
    // resolves operand types. We infer each local's type from the instruction that produces its stored
    // value (single-assignment dominates decompiled code); unresolved slots default to System.Object,
    // value-type locals are detected via `ldloca; initobj T`. Types are best-effort — enough for the
    // method to rebuild and decompile cleanly; refine later if exact types are needed.
    static bool IsStloc(Code c)=> c==Code.Stloc||c==Code.Stloc_S||c==Code.Stloc_0||c==Code.Stloc_1||c==Code.Stloc_2||c==Code.Stloc_3;
    static bool IsLdloc(Code c)=> c==Code.Ldloc||c==Code.Ldloc_S||c==Code.Ldloc_0||c==Code.Ldloc_1||c==Code.Ldloc_2||c==Code.Ldloc_3;
    static int LocalIndex(Instruction ins){
        switch(ins.OpCode.Code){
            case Code.Ldloc_0: case Code.Stloc_0: return 0;
            case Code.Ldloc_1: case Code.Stloc_1: return 1;
            case Code.Ldloc_2: case Code.Stloc_2: return 2;
            case Code.Ldloc_3: case Code.Stloc_3: return 3;
            case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloca: case Code.Ldloca_S:
            case Code.Stloc: case Code.Stloc_S:
                if(ins.Operand is Local lv) return lv.Index;
                if(ins.Operand is IVariable iv) return iv.Index;
                try { return Convert.ToInt32(ins.Operand); } catch { return -1; }
        }
        return -1;
    }
    // Close open type variables (!0, !1, ...) against the declaring TypeSpec. dnlib's internal
    // GenericArguments helper is not public, so do the small recursive substitution we need here.
    // Method variables (!!0) are deliberately left untouched because this helper only owns TYPE args.
    static TypeSig SubstituteTypeVars(TypeSig t, IList<TypeSig> typeArgs){
        if(t==null||typeArgs==null||typeArgs.Count==0) return t;
        if(t is GenericVar gv){
            uint n=gv.Number;
            return n<(uint)typeArgs.Count ? typeArgs[(int)n] : t;
        }
        if(t is GenericInstSig gi){
            var a=new List<TypeSig>(gi.GenericArguments.Count);
            foreach(var x in gi.GenericArguments) a.Add(SubstituteTypeVars(x,typeArgs));
            return new GenericInstSig(gi.GenericType,a);
        }
        if(t is SZArraySig sz) return new SZArraySig(SubstituteTypeVars(sz.Next,typeArgs));
        if(t is ArraySig ar) return new ArraySig(SubstituteTypeVars(ar.Next,typeArgs),ar.Rank,ar.Sizes,ar.LowerBounds);
        if(t is PtrSig ps) return new PtrSig(SubstituteTypeVars(ps.Next,typeArgs));
        if(t is ByRefSig br) return new ByRefSig(SubstituteTypeVars(br.Next,typeArgs));
        if(t is PinnedSig pin) return new PinnedSig(SubstituteTypeVars(pin.Next,typeArgs));
        return t;
    }
    // v0.7.8 — dnlib can expose a closed TypeSpec either directly as GenericInstSig or
    // wrapped by ClassOrValueTypeSig/TypeSpec. Normalize all forms before substituting !0.
    static GenericInstSig GetGenericInstance(TypeSig t){
        if(t==null) return null;
        t=StripByRef(t);
        try {
            if(t is GenericInstSig direct) return direct;
            if(t is ClassOrValueTypeSig cv && cv.TypeDefOrRef is TypeSpec ts1){
                var inner=StripByRef(ts1.TypeSig);
                if(inner is GenericInstSig wrapped) return wrapped;
            }
            var tdr=t.ToTypeDefOrRef();
            if(tdr is TypeSpec ts2){
                var inner=StripByRef(ts2.TypeSig);
                if(inner is GenericInstSig wrapped) return wrapped;
            }
        } catch { }
        return null;
    }

    static TypeSig CloseDeclaringTypeArgs(TypeSig t, ITypeDefOrRef declaringType){
        if(t==null||declaringType==null) return t;
        try {
            var gi=GetGenericInstance(declaringType.ToTypeSig());
            if(gi!=null) return SubstituteTypeVars(t,gi.GenericArguments);
        } catch { }
        return t;
    }

    static TypeSig SubstituteMethodVars(TypeSig t, IList<TypeSig> methodArgs){
        if(t==null||methodArgs==null||methodArgs.Count==0) return t;
        if(t is GenericMVar mv){
            uint n=mv.Number;
            return n<(uint)methodArgs.Count ? methodArgs[(int)n] : t;
        }
        if(t is GenericInstSig gi){
            var a=new List<TypeSig>(gi.GenericArguments.Count);
            foreach(var x in gi.GenericArguments) a.Add(SubstituteMethodVars(x,methodArgs));
            return new GenericInstSig(gi.GenericType,a);
        }
        if(t is SZArraySig sz) return new SZArraySig(SubstituteMethodVars(sz.Next,methodArgs));
        if(t is ArraySig ar) return new ArraySig(SubstituteMethodVars(ar.Next,methodArgs),ar.Rank,ar.Sizes,ar.LowerBounds);
        if(t is PtrSig ps) return new PtrSig(SubstituteMethodVars(ps.Next,methodArgs));
        if(t is ByRefSig br) return new ByRefSig(SubstituteMethodVars(br.Next,methodArgs));
        if(t is PinnedSig pin) return new PinnedSig(SubstituteMethodVars(pin.Next,methodArgs));
        return t;
    }

    static TypeSig CloseMethodTypeArgs(TypeSig t, IMethod method){
        if(t==null||method==null) return t;
        t=CloseDeclaringTypeArgs(t,method.DeclaringType);
        try {
            if(method is MethodSpec ms && ms.GenericInstMethodSig!=null)
                t=SubstituteMethodVars(t,ms.GenericInstMethodSig.GenericArguments);
        } catch { }
        return t;
    }

    static string GenericDefinitionKey(TypeSig t){
        t=StripByRef(t);
        try {
            var gi=GetGenericInstance(t);
            if(gi!=null) return gi.GenericType?.TypeDefOrRef?.FullName ?? "";
            if(t is ClassOrValueTypeSig cv){
                if(cv.TypeDefOrRef is TypeSpec ts){
                    var inner=StripByRef(ts.TypeSig);
                    var innerGi=GetGenericInstance(inner);
                    if(innerGi!=null) return innerGi.GenericType?.TypeDefOrRef?.FullName ?? "";
                }
                return cv.TypeDefOrRef?.FullName ?? "";
            }
        } catch { }
        return "";
    }

    static TypeSig CloseTypeVarsAgainstReceiver(TypeSig t, TypeSig receiverType){
        if(t==null||receiverType==null) return t;
        try {
            var gi=GetGenericInstance(receiverType);
            if(gi!=null) return SubstituteTypeVars(t,gi.GenericArguments);
        } catch { }
        return t;
    }

    static TypeSig NearbyValueType(ModuleDefMD module, Instruction ins, TypeSig[] known){
        if(ins==null) return null;
        switch(ins.OpCode.Code){
            case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
            case Code.Ldloc: case Code.Ldloc_S:
            case Code.Ldloca: case Code.Ldloca_S: {
                int i=LocalIndex(ins);
                return known!=null&&i>=0&&i<known.Length?known[i]:null; }
            case Code.Ldfld: case Code.Ldsfld: case Code.Ldflda: case Code.Ldsflda: {
                var f=ins.Operand as IField;
                return CloseDeclaringTypeArgs(f?.FieldSig?.Type,f?.DeclaringType); }
            case Code.Call: case Code.Callvirt: {
                var im=ins.Operand as IMethod;
                return CloseMethodTypeArgs(im?.MethodSig?.RetType,im); }
            case Code.Newobj:
                return (ins.Operand as IMethod)?.DeclaringType?.ToTypeSig();
            case Code.Ldc_R8: case Code.Conv_R8: case Code.Conv_R_Un:
                return module.CorLibTypes.Double;
            case Code.Ldc_R4: case Code.Conv_R4:
                return module.CorLibTypes.Single;
            case Code.Ldc_I8: case Code.Conv_I8:
                return module.CorLibTypes.Int64;
            case Code.Ldc_I4: case Code.Ldc_I4_S: case Code.Ldc_I4_0: case Code.Ldc_I4_1:
            case Code.Ldc_I4_2: case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5:
            case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8: case Code.Ldc_I4_M1:
            case Code.Conv_I4:
                return module.CorLibTypes.Int32;
            case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un:
                return module.CorLibTypes.Boolean;
            default: return null;
        }
    }

    // Close an open return signature (!0/!!0) using the actual receiver found immediately before
    // the call. This repairs patterns such as Enumerator<!0>.get_Current and TableIDPool<!0>.get_Item
    // when the imported MemberRef is open but the receiver local/field is a closed TypeSpec.
    static TypeSig ContextualCallReturnType(ModuleDefMD module, IList<Instruction> ins, int callIndex,
        TypeSig[] known){
        if(callIndex<0||callIndex>=ins.Count) return null;
        var call=ins[callIndex];
        var im=call.Operand as IMethod;
        if(im==null) return null;
        TypeSig rt=CloseMethodTypeArgs(im.MethodSig?.RetType,im);
        if(rt==null||!HasGenericVar(rt)) return rt;

        string wanted=GenericDefinitionKey(im.DeclaringType?.ToTypeSig());
        TypeSig fallback=null;
        for(int j=callIndex-1;j>=0&&j>=callIndex-20;j--){
            var x=ins[j];
            var c=x.OpCode.Code;
            if(c==Code.Nop) continue;
            if(c==Code.Br||c==Code.Br_S||c==Code.Leave||c==Code.Leave_S||
               c==Code.Ret||c==Code.Throw||c==Code.Switch) break;
            TypeSig candidate=NearbyValueType(module,x,known);
            if(candidate==null) continue;
            fallback??=candidate;
            string got=GenericDefinitionKey(candidate);
            if(string.IsNullOrEmpty(wanted)||string.IsNullOrEmpty(got)||
               !string.Equals(wanted,got,StringComparison.Ordinal))
                continue;
            TypeSig closed=CloseTypeVarsAgainstReceiver(rt,candidate);
            if(closed!=null&&!HasGenericVar(closed)) return closed;
        }
        // For a zero-argument instance getter, the immediately preceding value is the receiver even
        // when its generic definition name is represented differently by dnlib (nested Enumerator).
        if(im.MethodSig?.HasThis==true && (im.MethodSig.Params?.Count??0)==0 && fallback!=null){
            TypeSig closed=CloseTypeVarsAgainstReceiver(rt,fallback);
            if(closed!=null&&!HasGenericVar(closed)) return closed;
        }
        return rt;
    }

    static TypeSig ContextualProducedType(ModuleDefMD module, IList<Instruction> ins, int producerIndex,
        IList<Parameter> pars, TypeSig[] known){
        if(producerIndex<0||producerIndex>=ins.Count) return null;
        var p=ins[producerIndex];
        if(p.OpCode.Code==Code.Call||p.OpCode.Code==Code.Callvirt){
            var ct=ContextualCallReturnType(module,ins,producerIndex,known);
            if(ct!=null) return ct;
        }

        if(p.OpCode.Code==Code.Ldelem_Ref){
            for(int j=producerIndex-1;j>=0&&j>=producerIndex-12;j--){
                TypeSig a=NearbyValueType(module,ins[j],known);
                TypeSig e=ElemOf(StripByRef(a));
                if(e!=null&&!IsObjectLocal(e)) return e;
            }
        }

        bool arithmetic=p.OpCode.Code==Code.Add||p.OpCode.Code==Code.Sub||
            p.OpCode.Code==Code.Mul||p.OpCode.Code==Code.Div||p.OpCode.Code==Code.Div_Un||
            p.OpCode.Code==Code.Rem||p.OpCode.Code==Code.Rem_Un||
            p.OpCode.Code==Code.And||p.OpCode.Code==Code.Or||p.OpCode.Code==Code.Xor;
        if(arithmetic){
            bool sawBool=false,sawDouble=false,sawSingle=false,sawI8=false,sawI4=false;
            for(int j=producerIndex-1;j>=0&&j>=producerIndex-16;j--){
                var x=ins[j];
                var c=x.OpCode.Code;
                if(c==Code.Br||c==Code.Br_S||c==Code.Leave||c==Code.Leave_S||
                   c==Code.Ret||c==Code.Throw||c==Code.Switch) break;
                TypeSig t=NearbyValueType(module,x,known);
                if(IsBooleanLocal(t)) sawBool=true;
                else if(IsNamed(t,"System.Double")) sawDouble=true;
                else if(IsNamed(t,"System.Single")) sawSingle=true;
                else if(IsNamed(t,"System.Int64")||IsNamed(t,"System.UInt64")) sawI8=true;
                else if(IsNamed(t,"System.Int32")||IsNamed(t,"System.UInt32")) sawI4=true;
                if(c==Code.Ceq||c==Code.Cgt||c==Code.Cgt_Un||c==Code.Clt||c==Code.Clt_Un) sawBool=true;
            }
            if((p.OpCode.Code==Code.And||p.OpCode.Code==Code.Or||p.OpCode.Code==Code.Xor)&&sawBool)
                return module.CorLibTypes.Boolean;
            if(sawDouble) return module.CorLibTypes.Double;
            if(sawSingle) return module.CorLibTypes.Single;
            if(sawI8) return module.CorLibTypes.Int64;
            if(sawI4) return module.CorLibTypes.Int32;
        }
        return ProducedType(module,p,pars,known);
    }

    static TypeSig ProducedType(ModuleDefMD m, Instruction p, IList<Parameter> pars, TypeSig[] known){
        if(p==null) return null;
        switch(p.OpCode.Code){
            case Code.Newobj:  return (p.Operand as IMethod)?.DeclaringType?.ToTypeSig();
            case Code.Call: case Code.Callvirt: {
                var im=p.Operand as IMethod;
                var rt=CloseMethodTypeArgs(im?.MethodSig?.RetType,im);
                return (rt!=null && rt.ElementType!=ElementType.Void)? rt : null; }
            case Code.Ldsfld: case Code.Ldfld: {
                var f=p.Operand as IField;
                return CloseDeclaringTypeArgs(f?.FieldSig?.Type,f?.DeclaringType); }
            case Code.Ldsflda: case Code.Ldflda: {
                var f=p.Operand as IField;
                var ft=CloseDeclaringTypeArgs(f?.FieldSig?.Type,f?.DeclaringType);
                return ft!=null?new ByRefSig(ft):null; }
            case Code.Castclass: case Code.Isinst: return (p.Operand as ITypeDefOrRef)?.ToTypeSig();
            case Code.Ldstr:   return m.CorLibTypes.String;
            case Code.Ldnull:  return m.CorLibTypes.Object;
            case Code.Box:     return m.CorLibTypes.Object;
            case Code.Newarr:  { var e=(p.Operand as ITypeDefOrRef)?.ToTypeSig(); return e!=null? new SZArraySig(e):null; }
            case Code.Ldc_I4: case Code.Ldc_I4_S: case Code.Ldc_I4_0: case Code.Ldc_I4_1:
            case Code.Ldc_I4_2: case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5:
            case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8: case Code.Ldc_I4_M1:
                return m.CorLibTypes.Int32;
            case Code.Ldc_I8:  return m.CorLibTypes.Int64;
            case Code.Ldc_R4:  return m.CorLibTypes.Single;
            case Code.Ldc_R8:  return m.CorLibTypes.Double;
            case Code.Ldarg_0: return ArgType(m,pars,0);
            case Code.Ldarg_1: return ArgType(m,pars,1);
            case Code.Ldarg_2: return ArgType(m,pars,2);
            case Code.Ldarg_3: return ArgType(m,pars,3);
            case Code.Ldarg: case Code.Ldarg_S: {
                if(p.Operand is Parameter pp) return pp.Type;
                return null; }
            case Code.Ldarga: case Code.Ldarga_S: {
                if(p.Operand is Parameter pp){ var pt=pp.Type; return pt!=null?new ByRefSig(pt):null; }
                return null; }
            case Code.Ldloc_0: return known!=null&&known.Length>0? known[0]:null;
            case Code.Ldloc_1: return known!=null&&known.Length>1? known[1]:null;
            case Code.Ldloc_2: return known!=null&&known.Length>2? known[2]:null;
            case Code.Ldloc_3: return known!=null&&known.Length>3? known[3]:null;
            case Code.Ldloc: case Code.Ldloc_S: {
                int i=LocalIndex(p); return known!=null&&i>=0&&i<known.Length?known[i]:null; }
            case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un:
                return m.CorLibTypes.Boolean;
            case Code.Unbox_Any: case Code.Ldobj: case Code.Ldelem:
                return (p.Operand as ITypeDefOrRef)?.ToTypeSig();
            case Code.Unbox: case Code.Ldelema: {
                var e=(p.Operand as ITypeDefOrRef)?.ToTypeSig();
                return e!=null?new ByRefSig(e):null; }
            case Code.Ldelem_I1: case Code.Ldelem_U1: case Code.Ldelem_I2: case Code.Ldelem_U2:
            case Code.Ldelem_I4: case Code.Ldelem_U4:
                return m.CorLibTypes.Int32;
            case Code.Ldelem_I8: return m.CorLibTypes.Int64;
            case Code.Ldelem_I: return m.CorLibTypes.IntPtr;
            case Code.Ldelem_R4: return m.CorLibTypes.Single;
            case Code.Ldelem_R8: return m.CorLibTypes.Double;
            case Code.Ldelem_Ref: return m.CorLibTypes.Object;
            case Code.Conv_I1: case Code.Conv_U1: case Code.Conv_I2: case Code.Conv_U2:
            case Code.Conv_I4: case Code.Conv_U4:
            case Code.Conv_Ovf_I1: case Code.Conv_Ovf_I1_Un: case Code.Conv_Ovf_U1: case Code.Conv_Ovf_U1_Un:
            case Code.Conv_Ovf_I2: case Code.Conv_Ovf_I2_Un: case Code.Conv_Ovf_U2: case Code.Conv_Ovf_U2_Un:
            case Code.Conv_Ovf_I4: case Code.Conv_Ovf_I4_Un: case Code.Conv_Ovf_U4: case Code.Conv_Ovf_U4_Un:
                return m.CorLibTypes.Int32;
            case Code.Conv_I8: case Code.Conv_Ovf_I8: case Code.Conv_Ovf_I8_Un:
                return m.CorLibTypes.Int64;
            case Code.Conv_U8: case Code.Conv_Ovf_U8: case Code.Conv_Ovf_U8_Un:
                return m.CorLibTypes.UInt64;
            case Code.Conv_I: case Code.Conv_Ovf_I: case Code.Conv_Ovf_I_Un:
                return m.CorLibTypes.IntPtr;
            case Code.Conv_U: case Code.Conv_Ovf_U: case Code.Conv_Ovf_U_Un:
                return m.CorLibTypes.UIntPtr;
            case Code.Conv_R4: return m.CorLibTypes.Single;
            case Code.Conv_R8: case Code.Conv_R_Un: return m.CorLibTypes.Double;
            case Code.Sizeof: return m.CorLibTypes.Int32;
            default: return null;
        }
    }
    static TypeSig ArgType(ModuleDefMD m, IList<Parameter> pars, int i){
        if(pars!=null && i>=0 && i<pars.Count) return pars[i].Type;
        return null;
    }
    static List<TypeSig> InferLocals(ModuleDefMD module, CilBody body, IList<Parameter> pars, int wantCount){
        var instrs=body.Instructions;
        int maxIdx=wantCount-1;
        foreach(var ins in instrs){ int i=LocalIndex(ins); if(i>maxIdx) maxIdx=i; }
        int L=maxIdx+1; if(L<=0) return new List<TypeSig>();
        var types=new TypeSig[L];
        for(int k=0;k<instrs.Count;k++){
            var ins=instrs[k];
            if(IsStloc(ins.OpCode.Code)){
                int i=LocalIndex(ins); if(i<0||i>=L||types[i]!=null) continue;
                var t=ProducedType(module, k>0?instrs[k-1]:null, pars, types);
                if(t!=null) types[i]=t;
            } else if(ins.OpCode.Code==Code.Ldloca||ins.OpCode.Code==Code.Ldloca_S){
                int i=LocalIndex(ins); if(i<0||i>=L) continue;
                if(k+1<instrs.Count && instrs[k+1].OpCode.Code==Code.Initobj && instrs[k+1].Operand is ITypeDefOrRef tdr)
                    types[i]=new ValueTypeSig(tdr);   // ldloca i; initobj T => local i is value type T
            }
        }
        var list=new List<TypeSig>(L);
        for(int i=0;i<L;i++) list.Add(types[i] ?? module.CorLibTypes.Object);
        return list;
    }

    // ==== v0.7.5 semantic local refinement =====================================================
    // Index files from older captures often contain a correct local COUNT/order but use System.Object,
    // open !0/!!0, or Int32-as-Boolean placeholders. Keep the order and operand binding from v0.7.3,
    // then strengthen only weak slots from exact producers/consumers already present in the translated IL.
    class LocalTypeVote { public TypeSig Type; public int Score, Hits; }

    static TypeSig Unpin(TypeSig t){
        while(t is PinnedSig p) t=p.Next;
        return t;
    }
    static string TypeKey(TypeSig t){
        t=Unpin(t); return t?.FullName ?? "";
    }
    static bool IsNamed(TypeSig t,string fullName)=>
        string.Equals(TypeKey(t),fullName,StringComparison.Ordinal);
    static bool IsObjectLocal(TypeSig t)=>IsNamed(t,"System.Object");
    static bool IsBooleanLocal(TypeSig t)=>IsNamed(t,"System.Boolean");
    static bool IsInt32Local(TypeSig t)=>IsNamed(t,"System.Int32");
    static bool IsWeakLocalType(TypeSig t)=>t==null||IsObjectLocal(t)||HasGenericVar(t);

    static TypeSig DeclaringInstanceType(IMethod m){
        try { return m?.DeclaringType?.ToTypeSig(); } catch { return null; }
    }
    static TypeSig LastParameterType(IMethod m){
        try {
            var sig=m?.MethodSig;
            if(sig==null||sig.Params.Count==0) return null;
            return CloseMethodTypeArgs(sig.Params[sig.Params.Count-1],m);
        } catch { return null; }
    }
    static TypeSig StripByRef(TypeSig t){
        t=Unpin(t);
        return t is ByRefSig br ? br.Next : t;
    }

    static void AddLocalVote(Dictionary<int,Dictionary<string,LocalTypeVote>> all,
        int index, TypeSig type, int score){
        type=StripByRef(type);
        if(index<0||type==null||score<=0||HasGenericVar(type)) return;
        string key=TypeKey(type); if(string.IsNullOrEmpty(key)) return;
        if(!all.TryGetValue(index,out var byType)) all[index]=byType=new Dictionary<string,LocalTypeVote>();
        if(!byType.TryGetValue(key,out var v)) byType[key]=v=new LocalTypeVote{Type=type};
        v.Score+=score; v.Hits++;
    }

    static bool IsBooleanProducer(Instruction p, TypeSig[] known){
        if(p==null) return false;
        switch(p.OpCode.Code){
            case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un:
                return true;
            case Code.Ldc_I4_0: case Code.Ldc_I4_1:
                return true;
            case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
            case Code.Ldloc: case Code.Ldloc_S: {
                int i=LocalIndex(p);
                return known!=null&&i>=0&&i<known.Length&&IsBooleanLocal(known[i]); }
            default: return false;
        }
    }

    static bool LooksBooleanLocal(CilBody body,int index){
        int yes=0,no=0;
        var known=body.Variables.Select(v=>v.Type).ToArray();
        var ins=body.Instructions;
        for(int k=0;k<ins.Count;k++){
            if(!IsStloc(ins[k].OpCode.Code)||LocalIndex(ins[k])!=index) continue;
            var p=PrevReal(ins,k);
            if(IsBooleanProducer(p,known)) yes++; else no++;
        }
        return yes>0&&no==0;
    }

    static int RefineWeakLocalTypes(ModuleDefMD module, CilBody body, MethodDef method,
        IList<Parameter> pars, out int booleanRefined){
        booleanRefined=0; int total=0;
        if(body==null||body.Variables.Count==0) return 0;
        var ins=body.Instructions;

        // Multiple passes allow V3 <- get_Current and V5 <- V3 chains to benefit from earlier changes.
        for(int pass=0;pass<4;pass++){
            var known=body.Variables.Select(v=>v.Type).ToArray();
            var votes=new Dictionary<int,Dictionary<string,LocalTypeVote>>();

            for(int k=0;k<ins.Count;k++){
                var cur=ins[k]; int idx=LocalIndex(cur);
                if(idx<0||idx>=body.Variables.Count) continue;

                if(IsStloc(cur.OpCode.Code)){
                    var p=PrevReal(ins,k);
                    int producerIndex=k-1;
                    while(producerIndex>=0&&ins[producerIndex].OpCode.Code==Code.Nop) producerIndex--;
                    TypeSig t=ContextualProducedType(module,ins,producerIndex,pars,known);
                    int score=IsBooleanProducer(p,known)?12:
                        (p!=null&&(p.OpCode.Code==Code.Call||p.OpCode.Code==Code.Callvirt||
                                  p.OpCode.Code==Code.Newobj||p.OpCode.Code==Code.Ldfld||
                                  p.OpCode.Code==Code.Ldsfld)?10:7);
                    AddLocalVote(votes,idx,t,score);
                    continue;
                }

                if(cur.OpCode.Code==Code.Ldloca||cur.OpCode.Code==Code.Ldloca_S){
                    // Address receiver: scan a small straight-line window for initobj or an instance call.
                    for(int j=k+1;j<ins.Count&&j<=k+10;j++){
                        var n=ins[j]; var c=n.OpCode.Code;
                        if(c==Code.Nop) continue;
                        if(c==Code.Initobj && n.Operand is ITypeDefOrRef it){
                            AddLocalVote(votes,idx,it.ToTypeSig(),14); break;
                        }
                        if((c==Code.Call||c==Code.Callvirt) && n.Operand is IMethod im){
                            var sig=im.MethodSig;
                            if(sig!=null&&sig.HasThis) AddLocalVote(votes,idx,DeclaringInstanceType(im),12);
                            else AddLocalVote(votes,idx,LastParameterType(im),8);
                            break;
                        }
                        if(IsStloc(c)||c==Code.Ret||c==Code.Throw||c==Code.Br||c==Code.Br_S||
                           c==Code.Leave||c==Code.Leave_S) break;
                    }
                    continue;
                }

                if(IsLdloc(cur.OpCode.Code)){
                    var n=NextReal(ins,k); if(n==null) continue;
                    switch(n.OpCode.Code){
                        case Code.Ldfld: case Code.Ldflda:
                            AddLocalVote(votes,idx,(n.Operand as IField)?.DeclaringType?.ToTypeSig(),10);
                            break;
                        case Code.Stfld:
                            AddLocalVote(votes,idx,CloseDeclaringTypeArgs((n.Operand as IField)?.FieldSig?.Type,
                                (n.Operand as IField)?.DeclaringType),9);
                            break;
                        case Code.Call: case Code.Callvirt:
                            if(n.Operand is IMethod im){
                                var sig=im.MethodSig;
                                if(sig!=null&&sig.HasThis&&sig.Params.Count==0)
                                    AddLocalVote(votes,idx,DeclaringInstanceType(im),10);
                                else AddLocalVote(votes,idx,LastParameterType(im),7);
                            }
                            break;
                        case Code.Box: case Code.Unbox_Any: case Code.Castclass: case Code.Isinst:
                            AddLocalVote(votes,idx,(n.Operand as ITypeDefOrRef)?.ToTypeSig(),8);
                            break;
                        case Code.Brtrue: case Code.Brtrue_S: case Code.Brfalse: case Code.Brfalse_S:
                            AddLocalVote(votes,idx,module.CorLibTypes.Boolean,3);
                            break;
                        case Code.Switch:
                            AddLocalVote(votes,idx,module.CorLibTypes.Int32,5);
                            break;
                        case Code.Ret:
                            AddLocalVote(votes,idx,CloseMethodTypeArgs(method?.MethodSig?.RetType,method),8);
                            break;
                    }
                }
            }

            int changedThisPass=0;
            foreach(var kv in votes){
                int i=kv.Key; if(i<0||i>=body.Variables.Count) continue;
                var ranked=kv.Value.Values.OrderByDescending(v=>v.Score).ThenByDescending(v=>v.Hits).ToList();
                if(ranked.Count==0) continue;
                var best=ranked[0];
                int second=ranked.Count>1?ranked[1].Score:0;
                TypeSig current=body.Variables[i].Type;
                TypeSig candidate=best.Type;
                if(candidate==null||HasGenericVar(candidate)||TypeKey(current)==TypeKey(candidate)) continue;

                bool allow=IsWeakLocalType(current);
                if(!allow && IsInt32Local(current)&&IsBooleanLocal(candidate)&&LooksBooleanLocal(body,i))
                    allow=true;
                if(!allow) continue;
                if(best.Score<7) continue;
                if(second>0&&best.Score<second+2&&best.Score<12) continue;

                bool wasPinned=current is PinnedSig;
                body.Variables[i].Type=wasPinned?new PinnedSig(candidate):candidate;
                if(IsBooleanLocal(candidate)) booleanRefined++;
                total++; changedThisPass++;
            }
            if(changedThisPass==0) break;
        }
        return total;
    }


    // v0.7.8 — follow a local receiver back to its nearest definition. Some DNGuard local
    // signatures retain only a bare nested TypeRef (for example List<T>.Enumerator without T), while
    // the concrete generic argument survives on the producer chain:
    //     ldfld List<ObjectInfo> -> call GetEnumerator() -> stloc enumerator
    // ContextualProducedType can close GetEnumerator's open return against the concrete List<ObjectInfo>
    // receiver. The result can then close get_Current's !0 exactly.
    static TypeSig TraceNearestLocalProducerType(ModuleDefMD module, IList<Instruction> instructions,
        int beforeIndex, int localIndex, IList<Parameter> pars, TypeSig[] known){
        if(instructions==null||localIndex<0||beforeIndex<=0) return null;

        for(int k=Math.Min(beforeIndex-1,instructions.Count-1);k>=0;k--){
            var store=instructions[k];
            if(LocalIndex(store)!=localIndex||!IsStloc(store.OpCode.Code)) continue;

            int producer=k-1;
            while(producer>=0&&instructions[producer].OpCode.Code==Code.Nop) producer--;
            if(producer<0) return null;

            TypeSig produced=ContextualProducedType(module,instructions,producer,pars,known);
            produced=StripByRef(produced);
            if(produced!=null&&!HasGenericVar(produced)&&!IsObjectLocal(produced))
                return produced;

            // Use only the nearest reaching textual definition. Crossing an earlier assignment would
            // merge unrelated control-flow values and would no longer be exact evidence.
            return null;
        }
        return null;
    }

    // v0.7.8 — final exact-evidence closure for residual !0/!!0 locals outside a legitimate generic
    // context. The ordinary refinement pass is intentionally conservative and can miss a local when
    // producer and consumer evidence are separated by control flow. This pass scans every definition
    // and use and only applies a concrete type when the strongest evidence has a unique winner.
    static int RepairResidualOpenGenericLocals(ModuleDefMD module, CilBody body, MethodDef method,
        IList<Parameter> pars){
        if(body==null||method==null) return 0;
        if(method.HasGenericParameters||
           (method.DeclaringType!=null&&method.DeclaringType.HasGenericParameters))
            return 0;

        int fixedCount=0;
        var instructions=body.Instructions;
        var known=body.Variables.Select(v=>v.Type).ToArray();

        for(int localIndex=0;localIndex<body.Variables.Count;localIndex++){
            TypeSig current=body.Variables[localIndex].Type;
            if(!HasGenericVar(current)) continue;

            var candidates=new Dictionary<string,LocalTypeVote>();
            void Vote(TypeSig t,int score){
                t=StripByRef(t);
                if(t==null||HasGenericVar(t)||IsObjectLocal(t)) return;
                string key=TypeKey(t);
                if(string.IsNullOrEmpty(key)) return;
                if(!candidates.TryGetValue(key,out var v))
                    candidates[key]=new LocalTypeVote{Type=t,Score=score,Hits=1};
                else { v.Score+=score; v.Hits++; }
            }

            for(int k=0;k<instructions.Count;k++){
                var ins=instructions[k];
                if(LocalIndex(ins)!=localIndex) continue;

                if(IsStloc(ins.OpCode.Code)){
                    int producer=k-1;
                    while(producer>=0&&instructions[producer].OpCode.Code==Code.Nop) producer--;
                    if(producer>=0){
                        TypeSig produced=ContextualProducedType(module,instructions,producer,pars,known);
                        Vote(produced,16);

                        // Explicit get_Current fallback: close the getter's !0 against the nearest
                        // receiver local/address, even when dnlib represents nested Enumerator names
                        // differently and the normal generic-definition comparison misses.
                        var p= instructions[producer];
                        if((p.OpCode.Code==Code.Call||p.OpCode.Code==Code.Callvirt) &&
                           p.Operand is IMethod pim && pim.MethodSig?.HasThis==true &&
                           (pim.MethodSig.Params?.Count??0)==0){
                            TypeSig openRet=CloseMethodTypeArgs(pim.MethodSig.RetType,pim);
                            if(openRet!=null&&HasGenericVar(openRet)){
                                for(int j=producer-1;j>=0&&j>=producer-24;j--){
                                    var rc=instructions[j].OpCode.Code;
                                    if(rc==Code.Br||rc==Code.Br_S||rc==Code.Leave||rc==Code.Leave_S||
                                       rc==Code.Ret||rc==Code.Throw||rc==Code.Switch) break;
                                    TypeSig receiver=NearbyValueType(module,instructions[j],known);
                                    if(receiver==null) continue;

                                    TypeSig closed=CloseTypeVarsAgainstReceiver(openRet,receiver);
                                    if(closed!=null&&!HasGenericVar(closed)){
                                        Vote(closed,20);
                                        continue;
                                    }

                                    // The local may only contain a bare nested Enumerator TypeRef.
                                    // Trace its nearest stloc producer and recover Enumerator<T> from
                                    // the concrete collection receiver of GetEnumerator().
                                    int receiverLocal=LocalIndex(instructions[j]);
                                    if(receiverLocal>=0&&
                                       (IsLdloc(rc)||rc==Code.Ldloca||rc==Code.Ldloca_S)){
                                        TypeSig origin=TraceNearestLocalProducerType(module,instructions,
                                            j,receiverLocal,pars,known);
                                        TypeSig traced=CloseTypeVarsAgainstReceiver(openRet,origin);
                                        if(traced!=null&&!HasGenericVar(traced)) Vote(traced,24);
                                    }
                                }
                            }
                        }
                    }
                    continue;
                }

                if(IsLdloc(ins.OpCode.Code)){
                    var next=NextReal(instructions,k);
                    if(next==null) continue;
                    switch(next.OpCode.Code){
                        case Code.Ldfld: case Code.Ldflda:
                            Vote((next.Operand as IField)?.DeclaringType?.ToTypeSig(),18);
                            break;
                        case Code.Stfld:
                            Vote(CloseDeclaringTypeArgs((next.Operand as IField)?.FieldSig?.Type,
                                (next.Operand as IField)?.DeclaringType),14);
                            break;
                        case Code.Call: case Code.Callvirt:
                            if(next.Operand is IMethod im){
                                var sig=im.MethodSig;
                                if(sig!=null&&sig.HasThis&&(sig.Params?.Count??0)==0)
                                    Vote(DeclaringInstanceType(im),18);
                                else if(sig!=null&&!sig.HasThis&&(sig.Params?.Count??0)==1)
                                    Vote(CloseMethodTypeArgs(sig.Params[0],im),14);
                            }
                            break;
                        case Code.Box: case Code.Unbox_Any: case Code.Castclass: case Code.Isinst:
                            Vote((next.Operand as ITypeDefOrRef)?.ToTypeSig(),15);
                            break;
                        case Code.Ret:
                            Vote(CloseMethodTypeArgs(method.MethodSig?.RetType,method),14);
                            break;
                    }
                } else if(ins.OpCode.Code==Code.Ldloca||ins.OpCode.Code==Code.Ldloca_S){
                    for(int j=k+1;j<instructions.Count&&j<=k+12;j++){
                        var n=instructions[j]; var c=n.OpCode.Code;
                        if(c==Code.Nop) continue;
                        if(c==Code.Initobj){
                            Vote((n.Operand as ITypeDefOrRef)?.ToTypeSig(),20); break;
                        }
                        if((c==Code.Call||c==Code.Callvirt)&&n.Operand is IMethod im){
                            var sig=im.MethodSig;
                            if(sig!=null&&sig.HasThis) Vote(DeclaringInstanceType(im),18);
                            break;
                        }
                        if(IsStloc(c)||c==Code.Ret||c==Code.Throw||c==Code.Br||c==Code.Br_S||
                           c==Code.Leave||c==Code.Leave_S) break;
                    }
                }
            }

            var ranked=candidates.Values
                .OrderByDescending(v=>v.Score)
                .ThenByDescending(v=>v.Hits)
                .ToList();
            if(ranked.Count==0) continue;
            var best=ranked[0];
            int second=ranked.Count>1?ranked[1].Score:0;
            if(ranked.Count>1&&best.Score<second+4) continue;
            if(best.Score<14) continue;

            bool pinned=current is PinnedSig;
            body.Variables[localIndex].Type=pinned?new PinnedSig(best.Type):best.Type;
            known[localIndex]=body.Variables[localIndex].Type;
            fixedCount++;
        }
        return fixedCount;
    }

    // ==== v0.9 type-operand inference ==========================================================
    // Type tokens (newarr/castclass/box/ldelem/stelem/isinst/unbox.any/ldtoken/constrained/initobj/...)
    // are never captured by the shim, so they arrive as null operands. Infer each from IL context using a
    // forward abstract stack whose slots are either a known TypeSig or a PENDING type-op awaiting its
    // operand (so a later consumer can back-patch it). Anything still unknown falls back to System.Object
    // so the method never truncates. Best-effort; returns count set, out fallbackCount = #object fallbacks.
    class Slot { public TypeSig T; public Instruction Pending; }

    // ==== v0.8.0 high-confidence object-field retarget ==========================================
    // DNGuard preserves many FieldDef rows but collapses their signatures to System.Object. That
    // prevents the later array/address solver from seeing T[] receivers even when the body contains
    // exact evidence (newobj T -> stfld, stelem T, ldelema T, setter argument T, etc.).
    //
    // This pass is intentionally global and conservative:
    //   * only fields defined in this module whose CURRENT signature is exactly System.Object;
    //   * only concrete non-generic candidates;
    //   * commit one unique strong winner; conflicting concrete candidates reject the field;
    //   * System.Object[] from an unresolved newarr is treated as a weak wildcard, never a winner
    //     over a concrete T[] candidate.
    // After retargeting, locals and array/address operands are refined again.
    class FieldFlowSlot {
        public TypeSig T;
        public FieldDef SourceField;
        public Instruction Origin;
    }
    class FieldTypeVote {
        public TypeSig Type;
        public int Score, Hits, StrongScore, StrongHits;
        public HashSet<string> Sources=new HashSet<string>(StringComparer.Ordinal);
    }
    class FieldRetargetRecord {
        public string token, type, field, oldType, newType;
        public int score, hits;
        public string[] evidence;
    }
    class FieldRetargetResult {
        public int ObjectFieldsScanned, FieldsWithEvidence, Retargeted, ArrayRetargeted;
        public int Conflicted, WeakOnly, Rejected;
        public List<FieldRetargetRecord> Records=new List<FieldRetargetRecord>();
    }

    static FieldDef ResolveFieldDefSafe(IField field){
        if(field is FieldDef fd) return fd;
        try { return field?.ResolveFieldDef(); } catch { return null; }
    }
    static bool IsModuleObjectField(ModuleDefMD module, FieldDef field){
        return field!=null && field.DeclaringType?.Module==module && field.FieldSig!=null &&
               IsNamed(field.FieldSig.Type,"System.Object") &&
               field.DeclaringType!=null &&
               !string.Equals(field.DeclaringType.Name?.ToString(),"<Module>",StringComparison.Ordinal) &&
               (field.DeclaringType.FullName?.IndexOf("ZYXDNGuarder",StringComparison.OrdinalIgnoreCase)??-1)<0;
    }
    static TypeSig MakeSzArray(TypeSig element){
        element=StripByRef(element);
        return element==null?null:new SZArraySig(element);
    }
    static bool IsObjectArraySig(TypeSig type){
        type=Unpin(type);
        return type is SZArraySig sz && IsNamed(sz.Next,"System.Object");
    }
    static bool IsConcreteFieldCandidate(TypeSig type, bool allowObjectArray=true){
        type=StripByRef(type);
        if(type==null||HasGenericVar(type)||IsNamed(type,"System.Object")) return false;
        if(!allowObjectArray&&IsObjectArraySig(type)) return false;
        return !string.IsNullOrEmpty(TypeKey(type));
    }
    static void AddFieldVote(ModuleDefMD module,
        Dictionary<FieldDef,Dictionary<string,FieldTypeVote>> votes,
        FieldDef field, TypeSig type, int score, string source, bool strong=true){
        if(!IsModuleObjectField(module,field)||score<=0) return;
        type=StripByRef(type);
        if(!IsConcreteFieldCandidate(type,true)) return;
        // Object[] from `newarr object` is useful only as an "this is an array" hint. Keep it weak.
        if(IsObjectArraySig(type)) score=Math.Min(score,3);
        string key=TypeKey(type);
        if(!votes.TryGetValue(field,out var byType))
            votes[field]=byType=new Dictionary<string,FieldTypeVote>(StringComparer.Ordinal);
        if(!byType.TryGetValue(key,out var vote))
            byType[key]=vote=new FieldTypeVote{Type=type};
        vote.Score+=score; vote.Hits++;
        if(strong){ vote.StrongScore+=score; vote.StrongHits++; }
        if(!string.IsNullOrEmpty(source)&&vote.Sources.Count<24) vote.Sources.Add(source);
    }
    static FieldFlowSlot FieldPeek(List<FieldFlowSlot> stack,int fromTop){
        int index=stack.Count-1-fromTop;
        return index>=0&&index<stack.Count?stack[index]:null;
    }
    static TypeSig FieldOperandType(Instruction ins){
        try { return (ins?.Operand as ITypeDefOrRef)?.ToTypeSig(); } catch { return null; }
    }
    static TypeSig FieldArrayElementOperand(Instruction ins){
        if(ins==null) return null;
        switch(ins.OpCode.Code){
            case Code.Ldelem: case Code.Stelem: case Code.Ldelema:
                return FieldOperandType(ins);
            default: return null;
        }
    }
    static int StoreEvidenceScore(Instruction origin, TypeSig valueType){
        if(origin==null) return 8;
        switch(origin.OpCode.Code){
            case Code.Newobj: return 24;
            case Code.Newarr: return IsObjectArraySig(valueType)?3:20;
            case Code.Ldstr: return 22;
            case Code.Ldarg: case Code.Ldarg_S:
            case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3:
                return 18;
            case Code.Call: case Code.Callvirt: return 16;
            case Code.Ldloc: case Code.Ldloc_S:
            case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                return 14;
            case Code.Ldfld: case Code.Ldsfld: return 12;
            default: return 10;
        }
    }

    static void CollectObjectFieldEvidence(ModuleDefMD module, MethodDef method,
        Dictionary<FieldDef,Dictionary<string,FieldTypeVote>> votes){
        var body=method?.Body;
        if(body==null||body.Instructions.Count==0) return;
        var instructions=body.Instructions;
        var pars=new List<Parameter>(method.Parameters);
        var locals=body.Variables.Select(v=>v.Type).ToArray();
        var localSources=new FieldDef[locals.Length];
        var stack=new List<FieldFlowSlot>();
        var targets=new HashSet<Instruction>();
        foreach(var x in instructions){
            if(x.Operand is Instruction one) targets.Add(one);
            else if(x.Operand is IList<Instruction> many)
                foreach(var target in many) if(target!=null) targets.Add(target);
        }

        for(int k=0;k<instructions.Count;k++){
            var ins=instructions[k]; var code=ins.OpCode.Code;
            if(targets.Contains(ins)){
                stack.Clear();
                Array.Clear(localSources,0,localSources.Length);
            }

            // Evidence must be read before the instruction pops its operands.
            if(IsStloc(code)){
                int localIndex=LocalIndex(ins);
                var value=FieldPeek(stack,0);
                if(localIndex>=0&&localIndex<locals.Length){
                    localSources[localIndex]=value?.SourceField;
                    if(value?.SourceField!=null&&IsConcreteFieldCandidate(locals[localIndex],false))
                        AddFieldVote(module,votes,value.SourceField,locals[localIndex],10,
                            $"typed-local:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}/V_{localIndex}");
                }
            }
            else if(code==Code.Stfld||code==Code.Stsfld){
                var targetField=ResolveFieldDefSafe(ins.Operand as IField);
                var value=FieldPeek(stack,0);
                TypeSig candidate=value?.T;
                if(candidate==null&&value?.Origin?.OpCode.Code==Code.Newarr){
                    var element=FieldOperandType(value.Origin);
                    candidate=MakeSzArray(element);
                }
                if(candidate!=null)
                    AddFieldVote(module,votes,targetField,candidate,StoreEvidenceScore(value?.Origin,candidate),
                        $"store:{value?.Origin?.OpCode.Name??"stack"}:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}");
            }
            else if(code==Code.Ldelem||code==Code.Ldelema){
                var array=FieldPeek(stack,1);
                var element=FieldArrayElementOperand(ins);
                if(array?.SourceField!=null&&IsConcreteFieldCandidate(element,false))
                    AddFieldVote(module,votes,array.SourceField,MakeSzArray(element),14,
                        $"{ins.OpCode.Name}:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}");
            }
            else if(code==Code.Stelem){
                var array=FieldPeek(stack,2);
                var element=FieldArrayElementOperand(ins);
                if(array?.SourceField!=null&&IsConcreteFieldCandidate(element,false))
                    AddFieldVote(module,votes,array.SourceField,MakeSzArray(element),18,
                        $"stelem:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}");
            }
            else if(code==Code.Initobj||code==Code.Ldobj){
                var address=FieldPeek(stack,0);
                var type=FieldOperandType(ins);
                if(address?.SourceField!=null&&IsConcreteFieldCandidate(type,false))
                    AddFieldVote(module,votes,address.SourceField,type,18,
                        $"{ins.OpCode.Name}:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}");
            }
            else if(code==Code.Stobj||code==Code.Cpobj){
                var address=FieldPeek(stack,1);
                var type=FieldOperandType(ins);
                if(address?.SourceField!=null&&IsConcreteFieldCandidate(type,false))
                    AddFieldVote(module,votes,address.SourceField,type,18,
                        $"{ins.OpCode.Name}:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}");
            }
            else if(code==Code.Call||code==Code.Callvirt||code==Code.Newobj){
                var called=ins.Operand as IMethod; var sig=called?.MethodSig;
                if(sig!=null){
                    int parameterCount=sig.Params.Count;
                    for(int a=0;a<parameterCount;a++){
                        var argument=FieldPeek(stack,parameterCount-1-a);
                        if(argument?.SourceField==null) continue;
                        var parameterType=CloseDeclaringTypeArgs(sig.Params[a],called?.DeclaringType);
                        if(IsConcreteFieldCandidate(parameterType,false))
                            AddFieldVote(module,votes,argument.SourceField,parameterType,6,
                                $"call-arg:{called.Name}:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}",false);
                    }
                    if(sig.HasThis&&code!=Code.Newobj){
                        var receiver=FieldPeek(stack,parameterCount);
                        var receiverType=called?.DeclaringType?.ToTypeSig();
                        if(receiver?.SourceField!=null&&IsConcreteFieldCandidate(receiverType,false))
                            AddFieldVote(module,votes,receiver.SourceField,receiverType,6,
                                $"call-this:{called.Name}:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}",false);
                    }
                }
            }
            else if(code==Code.Ret){
                var value=FieldPeek(stack,0);
                TypeSig returnType=method.MethodSig?.RetType;
                if(value?.SourceField!=null&&returnType!=null&&
                   returnType.ElementType!=ElementType.Void&&IsConcreteFieldCandidate(returnType,false))
                    AddFieldVote(module,votes,value.SourceField,returnType,10,
                        $"return:0x{method.MDToken.Raw:X8}/IL_{ins.Offset:X4}");
            }

            if(code==Code.Dup){
                var top=FieldPeek(stack,0);
                stack.Add(new FieldFlowSlot{T=top?.T,SourceField=top?.SourceField,Origin=top?.Origin});
                continue;
            }

            int pop=PopCount(ins);
            if(pop>=999) stack.Clear();
            else for(int p=0;p<pop&&stack.Count>0;p++) stack.RemoveAt(stack.Count-1);

            if(IsLdloc(code)){
                int localIndex=LocalIndex(ins);
                stack.Add(new FieldFlowSlot{
                    T=localIndex>=0&&localIndex<locals.Length?locals[localIndex]:null,
                    SourceField=localIndex>=0&&localIndex<localSources.Length?localSources[localIndex]:null,
                    Origin=ins
                });
                continue;
            }
            if(code==Code.Ldloca||code==Code.Ldloca_S){
                int localIndex=LocalIndex(ins);
                var type=localIndex>=0&&localIndex<locals.Length?locals[localIndex]:null;
                stack.Add(new FieldFlowSlot{T=type!=null?new ByRefSig(type):null,Origin=ins});
                continue;
            }
            if(code==Code.Ldfld||code==Code.Ldsfld||code==Code.Ldflda||code==Code.Ldsflda){
                var fieldRef=ins.Operand as IField;
                var field=ResolveFieldDefSafe(fieldRef);
                TypeSig fieldType=CloseDeclaringTypeArgs(fieldRef?.FieldSig?.Type,fieldRef?.DeclaringType);
                if(code==Code.Ldflda||code==Code.Ldsflda)
                    fieldType=fieldType!=null?new ByRefSig(fieldType):null;
                stack.Add(new FieldFlowSlot{
                    T=fieldType,
                    SourceField=IsModuleObjectField(module,field)?field:null,
                    Origin=ins
                });
                continue;
            }

            int push; TypeSig pushedType;
            PushInfo(module,ins,pars,module.CorLibTypes.Object,out push,out pushedType);
            if(code==Code.Newarr){
                var element=FieldOperandType(ins);
                pushedType=MakeSzArray(element);
                push=1;
            }
            for(int p=0;p<push;p++)
                stack.Add(new FieldFlowSlot{T=pushedType,Origin=ins});

            if(code==Code.Br||code==Code.Br_S||code==Code.Ret||
               code==Code.Throw||code==Code.Rethrow)
                stack.Clear();
        }
    }

    static FieldRetargetResult RetargetObjectFieldsHighConfidence(ModuleDefMD module,string outDir){
        var result=new FieldRetargetResult();
        var votes=new Dictionary<FieldDef,Dictionary<string,FieldTypeVote>>();
        foreach(var field in module.GetTypes().SelectMany(t=>t.Fields))
            if(IsModuleObjectField(module,field)) result.ObjectFieldsScanned++;

        foreach(var method in module.GetTypes().SelectMany(t=>t.Methods))
            if(method.HasBody) CollectObjectFieldEvidence(module,method,votes);

        result.FieldsWithEvidence=votes.Count;
        foreach(var pair in votes.OrderBy(p=>p.Key.MDToken.Raw)){
            var field=pair.Key;
            var ranked=pair.Value.Values
                .OrderByDescending(v=>v.StrongScore).ThenByDescending(v=>v.StrongHits)
                .ThenByDescending(v=>v.Score).ThenByDescending(v=>v.Hits).ToList();
            if(ranked.Count==0){ result.Rejected++; continue; }

            // Object[] is a weak wildcard. A concrete array candidate can subsume it.
            var concreteArrays=ranked.Where(v=>v.Type is SZArraySig&&!IsObjectArraySig(v.Type)).ToList();
            if(concreteArrays.Count>0)
                ranked=ranked.Where(v=>!IsObjectArraySig(v.Type)).ToList();

            var best=ranked[0];
            var conflicting=ranked.Skip(1).Where(v=>v.StrongScore>=8).ToList();
            if(conflicting.Count>0){ result.Conflicted++; continue; }

            bool strong=best.StrongScore>=16 || (best.StrongScore>=12&&best.StrongHits>=2);
            if(!strong){ result.WeakOnly++; continue; }
            if(!IsConcreteFieldCandidate(best.Type,false)){ result.Rejected++; continue; }

            string oldType=field.FieldSig.Type?.FullName??"";
            string newType=best.Type?.FullName??"";
            if(string.Equals(oldType,newType,StringComparison.Ordinal)){ result.Rejected++; continue; }

            field.FieldSig.Type=best.Type;
            result.Retargeted++;
            if(best.Type is SZArraySig||best.Type is ArraySig) result.ArrayRetargeted++;
            result.Records.Add(new FieldRetargetRecord{
                token="0x"+field.MDToken.Raw.ToString("X8"),
                type=field.DeclaringType?.FullName??"",
                field=field.Name?.ToString()??"",
                oldType=oldType,newType=newType,score=best.StrongScore,hits=best.StrongHits,
                evidence=best.Sources.OrderBy(x=>x).ToArray()
            });
        }

        try {
            Directory.CreateDirectory(outDir);
            var opts=new JsonSerializerOptions{IncludeFields=true};
            File.WriteAllLines(Path.Combine(outDir,"field-retargets.jsonl"),
                result.Records.Select(r=>JsonSerializer.Serialize(r,opts)));
            File.WriteAllText(Path.Combine(outDir,"field-retarget-summary.json"),
                JsonSerializer.Serialize(new {
                    result.ObjectFieldsScanned,result.FieldsWithEvidence,result.Retargeted,
                    result.ArrayRetargeted,result.Conflicted,result.WeakOnly,result.Rejected
                },new JsonSerializerOptions{WriteIndented=true}));
        } catch { }
        return result;
    }

    static int RefineAfterFieldRetarget(ModuleDefMD module,IEnumerable<uint> methodTokens,
        ref int localRefined,ref int objectRefined,ref int initobjRepaired,
        Dictionary<string,int> objectByOpcode){
        int methodsChanged=0;
        foreach(uint token in methodTokens){
            var method=module.ResolveToken(token) as MethodDef;
            var body=method?.Body;
            if(body==null) continue;
            IList<Parameter> pars=new List<Parameter>(method.Parameters);
            int before=0;
            int lr=RefineWeakLocalTypes(module,body,method,pars,out _);
            if(lr>0){ localRefined+=lr; before+=lr; }
            int rg=RepairResidualOpenGenericLocals(module,body,method,pars);
            if(rg>0){ localRefined+=rg; before+=rg; }
            int obj=RefineObjectFlowOperands(module,body,method,out var byOpcode);
            if(obj>0){
                objectRefined+=obj; before+=obj;
                foreach(var kv in byOpcode)
                    objectByOpcode[kv.Key]=objectByOpcode.GetValueOrDefault(kv.Key)+kv.Value;
            }
            int io=RepairInitobjFromAddressProducer(body);
            if(io>0){ initobjRepaired+=io; before+=io; }
            if(before>0) methodsChanged++;
        }
        return methodsChanged;
    }

    static bool NeedsTypeOp(Code c){
        switch(c){
            case Code.Newarr: case Code.Castclass: case Code.Isinst: case Code.Box: case Code.Unbox:
            case Code.Unbox_Any: case Code.Ldtoken: case Code.Ldelem: case Code.Stelem: case Code.Ldelema:
            case Code.Constrained: case Code.Initobj: case Code.Ldobj: case Code.Stobj: case Code.Cpobj:
            case Code.Sizeof: case Code.Mkrefany: case Code.Refanyval:
                return true;
            default: return false;
        }
    }
    // Object-flow repair has two evidence modes:
    //   * producer-constrained memory operations (array/pointer/box/constrained), and
    //   * consumer-constrained casts/unbox operations, which are changed only when a concrete
    //     local/field/return/call parameter independently requires the replacement type.
    // ldtoken remains excluded because its exact target cannot be recovered from the consumer alone.
    static bool CanRefineObjectFlow(Code c){
        switch(c){
            case Code.Newarr: case Code.Ldelem: case Code.Stelem: case Code.Ldelema:
            case Code.Initobj: case Code.Ldobj: case Code.Stobj: case Code.Cpobj:
            case Code.Box: case Code.Constrained:
            case Code.Castclass: case Code.Isinst: case Code.Unbox: case Code.Unbox_Any:
                return true;
            default: return false;
        }
    }

    static bool SupportsGenericTypeOperand(Code c){
        switch(c){
            // ECMA-335 type operands that may legally reference !n/!!n in a real generic context.
            case Code.Newarr: case Code.Ldelem: case Code.Stelem: case Code.Ldelema:
            case Code.Initobj: case Code.Ldobj: case Code.Stobj: case Code.Cpobj:
            case Code.Box: case Code.Unbox: case Code.Unbox_Any: case Code.Constrained:
            case Code.Sizeof: case Code.Mkrefany: case Code.Refanyval:
                return true;
            default: return false;
        }
    }

    static ITypeDefOrRef ToTypeOperandRef(TypeSig type){
        if(type==null) return null;
        try{
            var direct=type.ToTypeDefOrRef();
            if(direct!=null) return direct;
            // GenericVar/GenericMVar and compound signatures containing them need a TypeSpec token.
            if(HasGenericVar(type)) return new TypeSpecUser(type);
        }catch{}
        return null;
    }
    static bool IsSystemObjectTypeOperand(Instruction ins){
        try {
            var t=ins?.Operand as ITypeDefOrRef;
            return t!=null && string.Equals(t.FullName,"System.Object",StringComparison.Ordinal);
        } catch { return false; }
    }
    static bool IsBoxableValueType(TypeSig type){
        type=StripByRef(type);
        if(type==null) return false;
        if(HasGenericVar(type)) return true;
        switch(type.ElementType){
            case ElementType.Boolean: case ElementType.Char:
            case ElementType.I1: case ElementType.U1:
            case ElementType.I2: case ElementType.U2:
            case ElementType.I4: case ElementType.U4:
            case ElementType.I8: case ElementType.U8:
            case ElementType.R4: case ElementType.R8:
            case ElementType.I: case ElementType.U:
            case ElementType.ValueType: case ElementType.TypedByRef:
                return true;
            case ElementType.GenericInst:
                return type is GenericInstSig gi && gi.GenericType is ValueTypeSig;
            default:
                return false;
        }
    }
    static TypeSig ElemOf(TypeSig t){
        if(t is SZArraySig sz) return sz.Next;
        if(t is ArraySig ar) return ar.Next;
        return null;
    }
    // reject inferred operands that reference a generic parameter (!0/!!0) — those come from an approx
    // open-generic member signature and are meaningless as a standalone type operand in this module.
    static bool HasGenericVar(TypeSig t){
        int guard=0;
        while(t!=null && guard++<64){
            var et=t.ElementType;
            if(et==ElementType.Var||et==ElementType.MVar) return true;
            if(t is GenericInstSig gi){
                if(gi.GenericType!=null && HasGenericVar(gi.GenericType)) return true;
                foreach(var a in gi.GenericArguments) if(HasGenericVar(a)) return true;
                return false;
            }
            t=t.Next;
        }
        return false;
    }
    static TypeSig PtrElem(TypeSig t){
        if(t is ByRefSig br) return br.Next;
        if(t is PtrSig p) return p.Next;
        return null;
    }
    static Slot Peek(List<Slot> st, int fromTop){ int i=st.Count-1-fromTop; return (i>=0 && i<st.Count)? st[i] : null; }
    static Instruction NextReal(IList<Instruction> ins, int k){
        for(int j=k+1;j<ins.Count;j++){ var c=ins[j].OpCode.Code; if(c!=Code.Nop) return ins[j]; }
        return null;
    }

    static int InferTypeOperands(ModuleDefMD m, CilBody body, MethodDef method, out int fallbackCount,
        bool refineObjectFallback=false, Dictionary<string,int> refinedByOpcode=null){
        fallbackCount=0; int inferred=0;
        var instrs=body.Instructions;
        var pars=new List<Parameter>(method.Parameters);
        var obj=m.CorLibTypes.Object;
        TypeSig[] localTypes=new TypeSig[body.Variables.Count];
        for(int i=0;i<localTypes.Length;i++) localTypes[i]=body.Variables[i].Type;
        var stack=new List<Slot>();
        var flowTargets=new HashSet<Instruction>();
        if(refineObjectFallback){
            foreach(var x in instrs){
                if(x.Operand is Instruction one) flowTargets.Add(one);
                else if(x.Operand is IList<Instruction> many) foreach(var target in many) if(target!=null) flowTargets.Add(target);
            }
        }

        bool Set(Instruction ins, TypeSig t){
            if(ins==null||t==null) return false;
            bool replacingObject = refineObjectFallback && CanRefineObjectFlow(ins.OpCode.Code) && IsSystemObjectTypeOperand(ins);
            if(ins.Operand!=null && !replacingObject) return false;
            bool genericContext=method.HasGenericParameters||
                (method.DeclaringType?.HasGenericParameters??false);
            bool genericOperandAllowed=genericContext&&SupportsGenericTypeOperand(ins.OpCode.Code);
            if(HasGenericVar(t)&&!genericOperandAllowed) return false;
            if(refineObjectFallback && IsNamed(t,"System.Object")) return false;
            var tdr=ToTypeOperandRef(t); if(tdr==null) return false;
            if(replacingObject && string.Equals(tdr.FullName,"System.Object",StringComparison.Ordinal)) return false;
            ins.Operand=tdr; inferred++;
            if(replacingObject && refinedByOpcode!=null){
                string op=ins.OpCode.Name??ins.OpCode.Code.ToString();
                refinedByOpcode[op]=refinedByOpcode.GetValueOrDefault(op)+1;
            }
            return true;
        }
        // consumer expects `expected` for a slot: if it's a pending type-op, back-patch it.
        void Patch(Slot s, TypeSig expected){
            if(s==null||s.Pending==null||expected==null) return;
            bool replaceable = s.Pending.Operand==null ||
                (refineObjectFallback && CanRefineObjectFlow(s.Pending.OpCode.Code) && IsSystemObjectTypeOperand(s.Pending));
            if(!replaceable) return;
            var t=expected;
            if(s.Pending.OpCode.Code==Code.Newarr){ var e=ElemOf(expected); if(e==null) return; t=e; }
            Set(s.Pending, t);
        }

        foreach(var ins in instrs){
            if(refineObjectFallback && flowTargets.Contains(ins)) stack.Clear();
            var code=ins.OpCode.Code;
            bool need = NeedsTypeOp(code) && (ins.Operand==null ||
                (refineObjectFallback && CanRefineObjectFlow(code) && IsSystemObjectTypeOperand(ins)));

            // producer-based (reads current stack) — exact where determinable
            if(need){
                switch(code){
                    case Code.Box: {
                        var valueType=StripByRef(Peek(stack,0)?.T);
                        if(!IsBoxableValueType(valueType)){
                            int currentIndex=instrs.IndexOf(ins);
                            int producerIndex=PreviousRealIndex(instrs,currentIndex);
                            valueType=StripByRef(ContextualProducedType(m,instrs,producerIndex,pars,localTypes));
                        }
                        if(IsBoxableValueType(valueType)) Set(ins,valueType);
                        break;
                    }
                    case Code.Mkrefany: { var e=PtrElem(Peek(stack,0)?.T); if(e!=null) Set(ins,e); break; }   // referent, not the ptr
                    // Sizeof consumes nothing -> no producer on the stack; leave for the object fallback.
                    case Code.Ldelem: case Code.Ldelema: { var a=Peek(stack,1); var e=ElemOf(a?.T); if(e!=null) Set(ins,e); break; }
                    case Code.Stelem: { var a=Peek(stack,2); var e=ElemOf(a?.T); if(e!=null) Set(ins,e); else { var val=Peek(stack,0); if(val?.T!=null) Set(ins,val.T);} break; }
                    case Code.Initobj: case Code.Ldobj: case Code.Refanyval: {
                        var a=Peek(stack,0); var e=PtrElem(a?.T);
                        if(e==null){
                            int currentIndex=instrs.IndexOf(ins);
                            int producerIndex=PreviousRealIndex(instrs,currentIndex);
                            var direct=ContextualProducedType(m,instrs,producerIndex,pars,localTypes);
                            e=PtrElem(direct);
                        }
                        if(e!=null) Set(ins,e);
                        break;
                    }
                    case Code.Stobj: { var val=Peek(stack,0); if(val?.T!=null) Set(ins,val.T); else { var a=Peek(stack,1); var e=PtrElem(a?.T); if(e!=null) Set(ins,e);} break; }
                    case Code.Cpobj: { var a=Peek(stack,1); var e=PtrElem(a?.T); if(e!=null) Set(ins,e); break; }
                    case Code.Constrained: {
                        // The constrained type is the RECEIVER (a managed pointer), which sits below the
                        // call's args — NOT the callee's declaring type. If branch-target stack clearing
                        // lost the receiver, recover only from the immediately preceding metadata/opcode
                        // producer; this remains non-circular and fixes ldflda !0 -> constrained. object.
                        int currentIndex=instrs.IndexOf(ins);
                        var nxt=NextReal(instrs,currentIndex);
                        int np=(nxt?.Operand as IMethod)?.MethodSig?.Params.Count ?? 0;
                        var e=StripByRef(PtrElem(Peek(stack,np)?.T));
                        if(!IsBoxableValueType(e)){
                            int producerIndex=PreviousRealIndex(instrs,currentIndex);
                            var direct=ContextualProducedType(m,instrs,producerIndex,pars,localTypes);
                            e=StripByRef(PtrElem(direct));
                        }
                        if(IsBoxableValueType(e)) Set(ins,e);
                        break;
                    }
                }
            }

            // apply stack effect (pop consumed -> push produced)
            ApplyEffect(m, ins, stack, pars, localTypes, obj, Patch, refineObjectFallback);
            if(refineObjectFallback && (code==Code.Br || code==Code.Br_S || code==Code.Ret ||
                code==Code.Throw || code==Code.Rethrow)) stack.Clear();
        }

        // consumer peephole for the still-pending producers (castclass/isinst/unbox.any/newarr): use the
        // immediate meaningful consumer's expected type.
        for(int k=0;k<instrs.Count;k++){
            var ins=instrs[k];
            var c=ins.OpCode.Code;
            bool unresolved = ins.Operand==null ||
                (refineObjectFallback && CanRefineObjectFlow(c) && IsSystemObjectTypeOperand(ins));
            if(!unresolved || !NeedsTypeOp(c)) continue;
            if(c==Code.Castclass||c==Code.Isinst||c==Code.Unbox_Any||c==Code.Unbox||c==Code.Newarr){
                var nxt=NextReal(instrs,k); TypeSig exp=ExpectedFromConsumer(m, nxt, method, localTypes);
                if(exp!=null){ if(c==Code.Newarr){ var e=ElemOf(exp); if(e!=null) Set(ins,e);} else Set(ins,exp); }
            }
        }

        // Initial pass only: any remaining null type-op -> System.Object (keeps the method decompilable).
        // The v0.7.9 refinement pass never creates new fallbacks; it only upgrades exact evidence.
        if(!refineObjectFallback){
            foreach(var ins in instrs){
                if(ins.Operand==null && NeedsTypeOp(ins.OpCode.Code)){
                    var tdr=obj.ToTypeDefOrRef();
                    if(tdr!=null){ ins.Operand=tdr; inferred++; fallbackCount++; }
                }
            }
        }
        return inferred;
    }

    // Re-run the abstract stack after all local-signature refinement. Up to four monotonic passes are
    // useful because resolving newarr T exposes T[] to later ldelema/ldelem/stelem, which in turn may
    // expose a managed-pointer type to initobj/ldobj/stobj. Only Object operands in the conservative
    // object-flow family are eligible. Generic !0/!!0 is accepted only for box/constrained inside a
    // real generic method/type; all other replacements require a concrete non-Object result.
    static int RefineObjectFlowOperands(ModuleDefMD m, CilBody body, MethodDef method,
        out Dictionary<string,int> byOpcode){
        byOpcode=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        int total=RefineAuthoritativeArraySinkOperands(m,method,body,byOpcode);
        for(int pass=0;pass<4;pass++){
            int changed=InferTypeOperands(m,body,method,out _,true,byOpcode);
            if(changed<=0) break;
            total+=changed;
        }
        return total;
    }

    // expected type a consumer instruction imposes on the value it consumes (best-effort, immediate use).
    static TypeSig ExpectedFromConsumer(ModuleDefMD m, Instruction c, MethodDef method, TypeSig[] locals){
        if(c==null) return null;
        switch(c.OpCode.Code){
            case Code.Stloc: case Code.Stloc_S: { int i=LocalIndex(c); return (i>=0&&i<locals.Length)?locals[i]:null; }
            case Code.Stloc_0: return locals.Length>0?locals[0]:null;
            case Code.Stloc_1: return locals.Length>1?locals[1]:null;
            case Code.Stloc_2: return locals.Length>2?locals[2]:null;
            case Code.Stloc_3: return locals.Length>3?locals[3]:null;
            case Code.Stfld: case Code.Stsfld: {
                var f=c.Operand as IField;
                return CloseDeclaringTypeArgs(f?.FieldSig?.Type,f?.DeclaringType); }
            case Code.Ret: { var rt=method.MethodSig?.RetType; return (rt!=null&&rt.ElementType!=ElementType.Void)?rt:null; }
            case Code.Castclass: case Code.Isinst: return (c.Operand as ITypeDefOrRef)?.ToTypeSig();
            default: return null;
        }
    }

    // Maintain the abstract stack across one instruction: back-patch pending consumed slots, pop, push.
    static void ApplyEffect(ModuleDefMD m, Instruction ins, List<Slot> stack, IList<Parameter> pars,
                            TypeSig[] locals, TypeSig obj, Action<Slot,TypeSig> patch,
                            bool refineObjectFallback=false){
        var code=ins.OpCode.Code;
        // dup FIRST (before pop/push): real net effect is +1, duplicating the actual top (type+pending).
        if(code==Code.Dup){ var top=stack.Count>0?stack[stack.Count-1]:new Slot{T=obj}; stack.Add(new Slot{T=top.T,Pending=top.Pending}); return; }
        // --- consumer back-patch for common typed consumers (before popping) ---
        if(code==Code.Stfld||code==Code.Stsfld){
            var field=ins.Operand as IField;
            patch(Peek(stack,0),CloseDeclaringTypeArgs(field?.FieldSig?.Type,field?.DeclaringType));
        }
        else if(IsStloc(code)){ int i=LocalIndex(ins); if(i>=0&&i<locals.Length) patch(Peek(stack,0), locals[i]); }
        else if(code==Code.Call||code==Code.Callvirt||code==Code.Newobj){
            var im=ins.Operand as IMethod; var sig=im?.MethodSig;
            if(sig!=null){
                int np=sig.Params.Count;
                bool hasThis = sig.HasThis && code!=Code.Newobj;
                // args are top np slots (this is below them if hasThis); close !0 against TypeSpec owner.
                for(int a=0;a<np;a++){
                    var sl=Peek(stack,np-1-a);
                    var parameterType=CloseDeclaringTypeArgs(sig.Params[a],im?.DeclaringType);
                    if(sl!=null) patch(sl,parameterType);
                }
            }
        }
        else if(code==Code.Stelem && ins.Operand is ITypeDefOrRef et){ patch(Peek(stack,0), et.ToTypeSig()); }

        // --- pop ---
        int pop=PopCount(ins);
        for(int i=0;i<pop;i++){ if(stack.Count>0) stack.RemoveAt(stack.Count-1); }

        // --- push ---
        if(IsLdloc(code)){ int i=LocalIndex(ins); stack.Add(new Slot{T=(i>=0&&i<locals.Length)?locals[i]:null}); return; }
        if(code==Code.Ldloca||code==Code.Ldloca_S){ int i=LocalIndex(ins); var lt=(i>=0&&i<locals.Length)?locals[i]:null; stack.Add(new Slot{T=lt!=null?new ByRefSig(lt):null}); return; }
        int push; TypeSig pt;
        PushInfo(m, ins, pars, obj, out push, out pt);
        for(int i=0;i<push;i++){
            bool repairPending = refineObjectFallback && CanRefineObjectFlow(code) && IsSystemObjectTypeOperand(ins);
            if(ins.Operand==null || repairPending){
                if(code==Code.Newarr||code==Code.Castclass||code==Code.Isinst||code==Code.Unbox_Any||code==Code.Unbox)
                    stack.Add(new Slot{Pending=ins});      // result type still unknown -> pending
                else stack.Add(new Slot{T=pt});
            } else {
                // A resolved type operand is real type evidence. The older solver discarded it and
                // therefore could not propagate T[] into a later ldelema/ldelem instruction.
                stack.Add(new Slot{T=pt});
            }
        }
    }

    static int PopCount(Instruction ins){
        var code=ins.OpCode.Code;
        if(code==Code.Call||code==Code.Callvirt||code==Code.Newobj){
            var sig=(ins.Operand as IMethod)?.MethodSig; int n=sig?.Params.Count ?? 0;
            if(sig!=null && sig.HasThis && code!=Code.Newobj) n++;
            return n;
        }
        if(code==Code.Calli){ var sig=(ins.Operand as MethodSig); int n=sig?.Params.Count??0; if(sig!=null&&sig.HasThis)n++; return n+1; }
        if(code==Code.Ret) return 0;   // treat as terminal; don't over-pop
        switch(ins.OpCode.StackBehaviourPop){
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi:
            case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8:
            case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi:
            case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: return 3;
            case StackBehaviour.PopAll: return 999;
            default: return 0;
        }
    }
    static void PushInfo(ModuleDefMD m, Instruction ins, IList<Parameter> pars, TypeSig obj, out int push, out TypeSig t){
        var code=ins.OpCode.Code; t=null;
        if(code==Code.Call||code==Code.Callvirt){
            var im=ins.Operand as IMethod;
            var rt=CloseDeclaringTypeArgs(im?.MethodSig?.RetType,im?.DeclaringType);
            if(rt!=null&&rt.ElementType!=ElementType.Void){push=1;t=rt;} else push=0; return;
        }
        if(code==Code.Newobj){ push=1; t=(ins.Operand as IMethod)?.DeclaringType?.ToTypeSig(); return; }
        if(code==Code.Calli){ var rt=(ins.Operand as MethodSig)?.RetType; if(rt!=null&&rt.ElementType!=ElementType.Void){push=1;t=rt;} else push=0; return; }
        switch(ins.OpCode.StackBehaviourPush){
            case StackBehaviour.Push0: push=0; return;
            case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8:
            case StackBehaviour.Pushr4: case StackBehaviour.Pushr8: case StackBehaviour.Pushref:
                push=1; t=ProducedType(m, ins, pars, null); return;
            case StackBehaviour.Push1_push1: push=2; return;
            default: push=0; return;
        }
    }

    // v0.7.2 — EH-flatten for inspection when clauses weren't captured: turn EH-only opcodes into
    // ordinary control flow so the real IL loads + decompiles (exception semantics are lost — this is
    // for READING the logic, not running). leave/leave.s -> br; endfinally -> nop; endfilter -> pop.
    static int FlattenEH(CilBody body){
        int n=0;
        foreach(var ins in body.Instructions){
            switch(ins.OpCode.Code){
                case Code.Leave: case Code.Leave_S: ins.OpCode=OpCodes.Br; n++; break;        // keep target Instruction
                case Code.Endfinally:               ins.OpCode=OpCodes.Nop; ins.Operand=null; n++; break; // (Endfault shares this)
                case Code.Endfilter:                ins.OpCode=OpCodes.Pop; ins.Operand=null; n++; break; // discard filter result
            }
        }
        body.ExceptionHandlers.Clear();
        return n;
    }

    // ---- virtual-token translation (v0.5) ----
    static readonly HashSet<byte> TOK_OPS = new HashSet<byte>{
        0x27,0x28,0x29,0x6F,0x70,0x71,0x72,0x73,0x74,0x75,0x79,0x7B,0x7C,0x7D,0x7E,0x7F,0x80,0x81,
        0x8C,0x8D,0x8F,0xA3,0xA4,0xA5,0xC2,0xC6,0xD0};
    static bool IsVirtual(uint t){
        uint tbl=t>>24;
        // +0x2B MethodSpec: DNGuard also virtualizes generic-method callsites (LINQ etc.). Omitting it
        // left 0x2B80xxxx raw in the IL => invalid token on write, silently, uncounted. Classify it so
        // it is counted + attempted (resolver imports the open generic method as an approximate ref).
        return (t&0x00800000)!=0 && (tbl==0x01||tbl==0x02||tbl==0x04||tbl==0x06||tbl==0x0A||tbl==0x1B||tbl==0x11||tbl==0x2B);
    }
    static int SingleOperandLen(byte op){
        if(op==0x0E||op==0x0F||op==0x10||op==0x11||op==0x12||op==0x13||op==0x1F) return 1;
        if((op>=0x2B&&op<=0x37)||op==0xDE) return 1;
        if(op==0x20||op==0x22) return 4;
        if(op==0x21||op==0x23) return 8;
        if((op>=0x38&&op<=0x44)||op==0xDD) return 4;
        if(TOK_OPS.Contains(op)) return 4;
        if(op==0x45) return -2;               // switch
        return 0;
    }
    static void PatchAt(byte[] c,int off,Dictionary<uint,uint> vmap,ref int patched,ref int unmapped){
        uint t=BitConverter.ToUInt32(c,off);
        if(!IsVirtual(t)) return;
        if(vmap.TryGetValue(t,out uint real)){ BitConverter.GetBytes(real).CopyTo(c,off); patched++; }
        else unmapped++;
    }
    // Walk raw IL; replace virtual token operands with real tokens from vmap (in place).
    static byte[] PatchTokens(byte[] c, Dictionary<uint,uint> vmap, out int patched, out int unmapped){
        patched=0; unmapped=0; int i=0,n=c.Length;
        while(i<n){
            byte op=c[i++];
            if(op==0xFE){
                if(i>=n) break; byte o2=c[i++];
                if(o2==0x06||o2==0x07||o2==0x15||o2==0x16||o2==0x1C){ if(i+4>n)break; PatchAt(c,i,vmap,ref patched,ref unmapped); i+=4; }
                else if(o2==0x09||o2==0x0A||o2==0x0B||o2==0x0C||o2==0x0D||o2==0x0E) i+=2;
                else if(o2==0x12||o2==0x19) i+=1;
                continue;
            }
            int L=SingleOperandLen(op);
            if(L==-2){ if(i+4>n)break; uint cnt=BitConverter.ToUInt32(c,i); i+=4+4*(int)cnt; }
            else if(TOK_OPS.Contains(op)){ if(i+4>n)break; PatchAt(c,i,vmap,ref patched,ref unmapped); i+=4; }
            else i+=L;
        }
        return c;
    }
    static Dictionary<uint,uint> LoadTokenmap(string path){
        var m=new Dictionary<uint,uint>();
        if(!File.Exists(path)) return m;
        try{
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            foreach(var p in doc.RootElement.EnumerateObject()){
                uint v=ParseHex(p.Name);
                if(p.Value.ValueKind==JsonValueKind.Object && p.Value.TryGetProperty("real",out var r))
                    m[v]=ParseHex(r.GetString());
            }
        }catch{}
        return m;
    }

    static uint ParseHex(string s){
        if(string.IsNullOrEmpty(s)) return 0;
        s = s.Trim(); if(s.StartsWith("0x")||s.StartsWith("0X")) s=s.Substring(2);
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)?v:0;
    }

    // ==== v0.8 external-ref resolver ============================================================
    class Hint { public int table; public string kind, ns, type, member, extToken; public bool hasToken, dynamicOnly; }

    static Dictionary<uint,Hint> LoadHints(string path){
        var m=new Dictionary<uint,Hint>();
        if(!File.Exists(path)) return m;
        try{
            using var doc=JsonDocument.Parse(File.ReadAllText(path));
            foreach(var p in doc.RootElement.EnumerateObject()){
                uint v=ParseHex(p.Name); if(v==0) continue;
                var o=p.Value; var h=new Hint();
                if(o.TryGetProperty("table",out var tb)&&tb.ValueKind==JsonValueKind.Number) h.table=tb.GetInt32();
                if(o.TryGetProperty("kind",out var k)) h.kind=k.GetString();
                if(o.TryGetProperty("ns",out var ns)) h.ns=ns.GetString();
                if(o.TryGetProperty("type",out var ty)) h.type=ty.GetString();
                if(o.TryGetProperty("member",out var mm)&&mm.ValueKind==JsonValueKind.String) h.member=mm.GetString();
                if(o.TryGetProperty("extToken",out var et)&&et.ValueKind==JsonValueKind.String) h.extToken=et.GetString();
                if(o.TryGetProperty("hasToken",out var ht)&&(ht.ValueKind==JsonValueKind.True||ht.ValueKind==JsonValueKind.False)) h.hasToken=ht.GetBoolean();
                if(o.TryGetProperty("dynamicOnly",out var d)&&(d.ValueKind==JsonValueKind.True||d.ValueKind==JsonValueKind.False)) h.dynamicOnly=d.GetBoolean();
                m[v]=h;
            }
        }catch{}
        return m;
    }

    // Walk raw IL (post-PatchTokens) and return (instructionOffset, virtualToken) for every token op
    // whose operand is STILL a virtual token = the unmapped ones the resolver must fix.
    static List<(int,uint)> CollectVirtualOperands(byte[] c){
        var outp=new List<(int,uint)>(); int i=0,n=c.Length;
        while(i<n){
            int insOff=i; byte op=c[i++];
            if(op==0xFE){
                if(i>=n) break; byte o2=c[i++];
                if(o2==0x06||o2==0x07||o2==0x15||o2==0x16||o2==0x1C){ if(i+4>n)break; uint t=BitConverter.ToUInt32(c,i); if(IsVirtual(t)) outp.Add((insOff,t)); i+=4; }
                else if(o2==0x09||o2==0x0A||o2==0x0B||o2==0x0C||o2==0x0D||o2==0x0E) i+=2;
                else if(o2==0x12||o2==0x19) i+=1;
                continue;
            }
            int L=SingleOperandLen(op);
            if(L==-2){ if(i+4>n)break; uint cnt=BitConverter.ToUInt32(c,i); i+=4+4*(int)cnt; }
            else if(TOK_OPS.Contains(op)){ if(i+4>n)break; uint t=BitConverter.ToUInt32(c,i); if(IsVirtual(t)) outp.Add((insOff,t)); i+=4; }
            else i+=L;
        }
        return outp;
    }

    // Resolve a hint (declaring type ns+name, member name, optional external def token) to an imported
    // module member. Methods: prefer EXACT match by external MethodDef token (disambiguates overloads),
    // else fall back to name ONLY if unique. Fields: match by name (unique). Returns null (no guessing)
    // when the type can't be resolved, or a method name is ambiguous with no token match.
    static IMemberRef ResolveRef(ModuleDefMD module, Importer importer,
                                 Dictionary<string,IMemberRef> cache, Dictionary<string,TypeDef> typeCache,
                                 Hint h, out bool isField, out bool approx){
        isField = h.kind=="field"; approx=false;
        if(string.IsNullOrEmpty(h.type)) return null;
        string ns=h.ns??"";
        string ckey=$"{h.kind}|{ns}|{h.type}|{h.member}|{h.extToken}";
        if(cache.TryGetValue(ckey, out var hit)){ approx=approxCache.Contains(ckey); return hit; }

        TypeDef td = ResolveTypeDef(module, typeCache, ns, h.type);
        if(td==null){ cache[ckey]=null; return null; }

        // GENERIC (v0.8.2): the hint carries only the OPEN definition (HashSet`1 / Enumerable::Where),
        // no instantiation type-args (a TypeSpec/MethodSpec is unrecoverable from the hint). Importing the
        // OPEN member gives a reference that LOADS + DECOMPILES readably (HashSet<T>::Contains) — good for
        // Track A reading — but the exact type-arg is lost. So resolve it (keeps the method un-truncated)
        // and flag APPROX so metrics don't count it as an exact/clean fix. Exact instantiated generics that
        // WERE captured resolve earlier via tokenmap (original TypeSpec MemberRef), never here.
        if(td.HasGenericParameters) approx=true;

        IMemberRef result=null;
        if(isField){
            FieldDef fd = FindField(td, h.member);
            if(fd!=null) result = importer.Import(fd);
        } else {
            MethodDef md = FindMethod(td, h.member, h.extToken);
            if(md!=null){
                if(md.HasGenericParameters) approx=true;   // generic METHOD (MethodSpec) — open, approximate
                result = importer.Import(md);
            }
        }
        cache[ckey]=result;
        if(approx && result!=null) approxCache.Add(ckey);
        return result;
    }
    static readonly HashSet<string> approxCache = new HashSet<string>();

    // Find the TypeDef for (ns,name): app TypeDef directly, else resolve the module's TypeRef to its
    // external definition via the assembly resolver (publish dir). Cached.
    static TypeDef ResolveTypeDef(ModuleDefMD module, Dictionary<string,TypeDef> cache, string ns, string name){
        string key=ns+"|"+name;
        if(cache.TryGetValue(key, out var c)) return c;
        TypeDef td=null;
        foreach(var t in module.GetTypes()) if(t.Name==name && (t.Namespace??"")==ns){ td=t; break; }
        if(td==null){
            foreach(var tr in module.GetTypeRefs()) if(tr.Name==name && (tr.Namespace??"")==ns){
                try { td = tr.Resolve(); } catch { td=null; }
                if(td!=null) break;
            }
        }
        cache[key]=td;
        return td;
    }

    static FieldDef FindField(TypeDef td, string name){
        if(string.IsNullOrEmpty(name)) return null;
        FieldDef found=null; int n=0;
        foreach(var f in td.Fields) if(f.Name==name){ found=f; n++; }
        return n==1 ? found : (n>1 ? found : null);   // fields are effectively unique by name
    }

    // Prefer exact match by external MethodDef token (overload-safe); else unique-by-name; else null.
    static MethodDef FindMethod(TypeDef td, string name, string extToken){
        if(string.IsNullOrEmpty(name)) return null;
        uint ext = ParseHex(extToken);           // MethodDef token in the DEFINING assembly
        uint extRid = ext & 0x00FFFFFF;
        MethodDef byName=null; int nName=0;
        foreach(var m in td.Methods){
            if(m.Name!=name) continue;
            if(ext!=0 && (m.MDToken.Raw==ext || m.Rid==extRid)) return m;   // exact def-token hit
            byName=m; nName++;
        }
        return nName==1 ? byName : null;         // ambiguous name + no token hit => don't guess
    }

    // Minimal JSON reader for our own meta.json (avoids extra deps). Robust enough for the fixed shape.
    static Meta ParseMeta(string j){
        var m = new Meta();
        m.token      = JStr(j,"token");
        m.name       = JStr(j,"name");
        m.ilSize     = JInt(j,"ilSize");
        m.maxStack   = JInt(j,"maxStack");
        m.ehCount    = JInt(j,"ehCount");
        m.hasLocalsBlob = JBool(j,"hasLocalsBlob");
        m.dynamicOnly   = JBool(j,"dynamicOnly");
        int lc = JIntIn(j, "\"locals\"", "count", out bool hasLoc);
        if(hasLoc){ m.locals = new LocalsMeta{ count=lc, cbSig=JIntIn(j,"\"locals\"","cbSig", out _) }; }
        // eh: parse array of objects
        int ehPos = j.IndexOf("\"eh\"");
        if(ehPos>=0){
            int lb=j.IndexOf('[',ehPos), rb = lb>=0? MatchBracket(j,lb):-1;
            if(lb>=0&&rb>lb){
                string arr=j.Substring(lb+1, rb-lb-1); int p=0;
                while(true){
                    int ob=arr.IndexOf('{',p); if(ob<0) break; int cb=arr.IndexOf('}',ob); if(cb<0) break;
                    string o=arr.Substring(ob,cb-ob+1);
                    m.eh.Add(new EhMeta{
                        ehNumber=JInt(o,"ehNumber"), flags=JInt(o,"flags"),
                        tryOffset=JInt(o,"tryOffset"), tryLength=JInt(o,"tryLength"),
                        handlerOffset=JInt(o,"handlerOffset"), handlerLength=JInt(o,"handlerLength"),
                        classTokenOrFilter=JStr(o,"classTokenOrFilter")});
                    p=cb+1;
                }
            }
        }
        return m;
    }
    static int MatchBracket(string s,int open){int d=0;for(int i=open;i<s.Length;i++){if(s[i]=='[')d++;else if(s[i]==']'){d--;if(d==0)return i;}}return -1;}
    static string JStr(string j,string k){int p=j.IndexOf("\""+k+"\"");if(p<0)return null;int c=j.IndexOf(':',p);if(c<0)return null;int q1=j.IndexOf('"',c+1);if(q1<0)return null;int q2=j.IndexOf('"',q1+1);if(q2<0)return null;return j.Substring(q1+1,q2-q1-1);}
    static int JInt(string j,string k){int p=j.IndexOf("\""+k+"\"");if(p<0)return 0;int c=j.IndexOf(':',p);if(c<0)return 0;int i=c+1;while(i<j.Length&&(j[i]==' '))i++;int s=i;while(i<j.Length&&(char.IsDigit(j[i])||j[i]=='-'))i++;return int.TryParse(j.Substring(s,i-s),out var v)?v:0;}
    static bool JBool(string j,string k){int p=j.IndexOf("\""+k+"\"");if(p<0)return false;int c=j.IndexOf(':',p);return c>=0&&j.IndexOf("true",c,StringComparison.Ordinal)==FirstNonSpace(j,c+1);}
    static int FirstNonSpace(string j,int i){while(i<j.Length&&j[i]==' ')i++;return i;}
    static int JIntIn(string j,string section,string key,out bool present){present=false;int p=j.IndexOf(section);if(p<0)return 0;int e=j.IndexOf('}',p);string seg=e>p?j.Substring(p,e-p):j.Substring(p);int kp=seg.IndexOf("\""+key+"\"");if(kp<0)return 0;present=true;return JInt(seg,key);}
}
