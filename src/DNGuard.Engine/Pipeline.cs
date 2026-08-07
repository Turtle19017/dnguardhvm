// Pipeline.cs — v1.0 UNIFIED PIPELINE (DNGuardUnpacker).
// One .NET console command that does BOTH indexing (C# port of index_corpus.py v6.5) AND rebuild, in a
// single process, with rich per-method logging ("solving each method"). Reuses every tested static helper
// from Program.cs (PatchTokens / ResolveRef / InferTypeOperands / InferLocals / TryDetectPrologue / ...)
// so behaviour matches the file-based --index path exactly.
//
//   DNGuardRebuilder --corpus <dir1> [dir2 ...] --module <dump.dll> --out <rebuilt.dll>
//                    [--target-ns LordsMobileBot] [--only-types A B] [--prologue-mode strip]
//                    [--eh-mode flatten] [--resolve-refs on|off] [--dep-dir <dir>] [--verbose] [--quiet]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

public partial class Program {

    // multi-value arg: collects tokens after `k` until the next `--flag`.
    static List<string> ArgList(string[] a, string k){
        var r=new List<string>();
        for(int i=0;i<a.Length;i++) if(string.Equals(a[i],k,StringComparison.OrdinalIgnoreCase)){
            for(int j=i+1;j<a.Length && !a[j].StartsWith("--"); j++) r.Add(a[j]);
        }
        return r;
    }
    static bool HasFlag(string[] a, string k){ foreach(var x in a) if(string.Equals(x,k,StringComparison.OrdinalIgnoreCase)) return true; return false; }

    // ===== token helpers (port of index_corpus.py) ============================================
    static readonly HashSet<uint> VIRT_TABLES = new(){0x01,0x02,0x04,0x06,0x0A,0x1B,0x11,0x2B};
    static bool IsVirtualTok(uint v)=> (v & 0x00800000)!=0 && VIRT_TABLES.Contains(v>>24);
    static bool TryTok(string s, out uint v){
        v=0; if(string.IsNullOrEmpty(s)) return false;
        var t=s.Trim(); if(t.StartsWith("0x")||t.StartsWith("0X")) t=t.Substring(2);
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    // ===== parsed callback ====================================================================
    sealed class CB {
        public string tokenIn, tokenResolved, hMethod, hField, hClass;
        public bool hasIdentity, idHasToken, idDynamicOnly;
        public string idKind, idMetaToken, idNs, idName, idDeclNs, idDeclName;
        public string Handle(){
            if(!string.IsNullOrEmpty(hMethod) && hMethod!="0x0") return hMethod;
            if(!string.IsNullOrEmpty(hField)  && hField !="0x0") return hField;
            if(!string.IsNullOrEmpty(hClass)  && hClass !="0x0") return hClass;
            return "0x0";
        }
        public uint RealToken(){ // first NON-virtual of (tokenResolved, tokenIn)
            if(TryTok(tokenResolved, out var r) && !IsVirtualTok(r)) return r;
            if(TryTok(tokenIn,       out var i) && !IsVirtualTok(i)) return i;
            return 0;
        }
    }
    static List<CB> ReadCallbacks(string path){
        var outp=new List<CB>();
        if(!File.Exists(path)) return outp;
        foreach(var line in File.ReadLines(path)){
            var s=line.Trim(); if(s.Length==0) continue;
            try { var cb=ParseCB(s); if(cb!=null) outp.Add(cb); } catch { }
        }
        return outp;
    }
    static CB ParseCB(string json){
        using var doc=JsonDocument.Parse(json);
        var r=doc.RootElement; var cb=new CB(); JsonElement e;
        if(r.TryGetProperty("tokenIn",out e)) cb.tokenIn=e.GetString();
        if(r.TryGetProperty("tokenResolved",out e)) cb.tokenResolved=e.GetString();
        if(r.TryGetProperty("hMethod",out e)) cb.hMethod=e.GetString();
        if(r.TryGetProperty("hField",out e))  cb.hField =e.GetString();
        if(r.TryGetProperty("hClass",out e))  cb.hClass =e.GetString();
        if(r.TryGetProperty("identity",out var id) && id.ValueKind==JsonValueKind.Object){
            cb.hasIdentity=true;
            if(id.TryGetProperty("kind",out e)) cb.idKind=e.GetString();
            if(id.TryGetProperty("metadataToken",out e)) cb.idMetaToken=e.GetString();
            if(id.TryGetProperty("hasToken",out e) && (e.ValueKind==JsonValueKind.True||e.ValueKind==JsonValueKind.False)) cb.idHasToken=e.GetBoolean();
            if(id.TryGetProperty("dynamicOnly",out e) && (e.ValueKind==JsonValueKind.True||e.ValueKind==JsonValueKind.False)) cb.idDynamicOnly=e.GetBoolean();
            if(id.TryGetProperty("ns",out e)) cb.idNs=e.GetString();
            if(id.TryGetProperty("name",out e)) cb.idName=e.GetString();
            if(id.TryGetProperty("declType",out var dt) && dt.ValueKind==JsonValueKind.Object){
                if(dt.TryGetProperty("ns",out e))   cb.idDeclNs  =e.GetString();
                if(dt.TryGetProperty("name",out e)) cb.idDeclName=e.GetString();
            }
        }
        return cb;
    }

    // ===== map builders (port of build_*_handle_real / build_tokenmap_union / build_hints) =====
    static Dictionary<string,uint> BuildLocalHandleReal(List<CB> cbs){
        var l=new Dictionary<string,uint>(); var conflict=new HashSet<string>();
        foreach(var c in cbs){ var h=c.Handle(); if(h=="0x0") continue; var r=c.RealToken(); if(r==0) continue;
            if(l.TryGetValue(h,out var ex)){ if(ex!=r) conflict.Add(h); } else l[h]=r; }
        foreach(var h in conflict) l.Remove(h);
        return l;
    }
    static Dictionary<uint,uint> BuildTokenmapUnion(List<CB> cbs, Dictionary<string,uint> g){
        var lh=BuildLocalHandleReal(cbs);
        var tm=new Dictionary<uint,uint>(); var bad=new HashSet<uint>();
        foreach(var c in cbs){
            var h=c.Handle(); if(h=="0x0") continue;
            if(!TryTok(c.tokenIn, out var v) || !IsVirtualTok(v)) continue;
            uint r=0;
            if(lh.TryGetValue(h,out var lr)) r=lr;                 // PREFER LOCAL
            else if(g.TryGetValue(h,out var gr)) r=gr;             // else global
            if(r==0 && (v>>24)==0x06){                             // identity-harvest MethodDef
                if(c.hasIdentity && TryTok(c.idMetaToken,out var mt) && (mt>>24)==0x06
                   && !IsVirtualTok(mt) && c.idHasToken && !c.idDynamicOnly) r=mt;
            }
            if(r==0) continue;
            if(tm.TryGetValue(v,out var ex)){ if(ex!=r) bad.Add(v); } else tm[v]=r;
        }
        foreach(var k in bad) tm.Remove(k);
        return tm;
    }
    static Dictionary<uint,Hint> BuildHints(List<CB> cbs, Dictionary<uint,uint> tm){
        var h=new Dictionary<uint,Hint>(); var bad=new HashSet<uint>();
        foreach(var c in cbs){
            if(!TryTok(c.tokenIn,out var v) || !IsVirtualTok(v)) continue;
            if(tm.ContainsKey(v)) continue;
            string ns,type,member;
            if(c.idKind=="class"){ ns=c.idNs??""; type=c.idName??""; member=null; }
            else { ns=c.idDeclNs??""; type=c.idDeclName??""; member=c.idName; }
            var ent=new Hint{ table=(int)(v>>24), kind=c.idKind, ns=ns, type=type, member=member,
                              extToken=c.idMetaToken, hasToken=c.idHasToken, dynamicOnly=c.idDynamicOnly };
            if(h.TryGetValue(v,out var ex)){ if(!HintEq(ex,ent)) bad.Add(v); } else h[v]=ent;
        }
        foreach(var k in bad) h.Remove(k);
        return h;
    }
    static bool HintEq(Hint a, Hint b)=> a.table==b.table && a.kind==b.kind && a.ns==b.ns && a.type==b.type
        && a.member==b.member && a.extToken==b.extToken && a.hasToken==b.hasToken && a.dynamicOnly==b.dynamicOnly;

    // ===== corpus record ======================================================================
    sealed class MRec {
        public int ilSize, maxStack, ehCount, localsCount, localsCbSig;
        public string ilSha, name, declNs, declName, scope, dir;
        public bool placeholder, dynamicOnly;
        public uint token;
    }
    static JsonElement? JField(JsonElement o, string k){ return o.TryGetProperty(k,out var e)?e:(JsonElement?)null; }
    static int JInt(JsonElement o, string k){ var e=JField(o,k); return (e.HasValue && e.Value.ValueKind==JsonValueKind.Number)?e.Value.GetInt32():0; }
    static string JS(JsonElement o, string k){ var e=JField(o,k); return (e.HasValue && e.Value.ValueKind==JsonValueKind.String)?e.Value.GetString():null; }
    static bool JB(JsonElement o, string k){ var e=JField(o,k); return e.HasValue && e.Value.ValueKind==JsonValueKind.True; }

    // Read one method dir's identity+method-info+locals meta. Returns null if not a valid target record.
    static MRec ReadRec(string dir, HashSet<string> onlyTypes, Counters ctr){
        JsonDocument idDoc, miDoc, locDoc=null;
        try { idDoc=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"identity.json"))); }
        catch { ctr.noIdentity++; return null; }
        using(idDoc){
            var id=idDoc.RootElement;
            if(!JB(id,"hasToken")){ ctr.noIdentity++; return null; }
            string declName=null, declNs=null;
            var dt=JField(id,"declType");
            if(dt.HasValue && dt.Value.ValueKind==JsonValueKind.Object){ declName=JS(dt.Value,"name"); declNs=JS(dt.Value,"ns"); }
            if(onlyTypes!=null && (declName==null || !onlyTypes.Contains(declName))){ ctr.skippedFilter++; return null; }
            try { miDoc=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"method-info.json"))); }
            catch { return null; }
            using(miDoc){
                var mi=miDoc.RootElement;
                string scope=JS(mi,"scope"); string tokS=JS(id,"metadataToken");
                if(scope==null || !TryTok(tokS, out var tok)){ ctr.noIdentity++; return null; }
                if(JB(mi,"placeholderSuspected")) ctr.placeholder++;
                if(JB(id,"dynamicOnly")) ctr.dynamic++;
                var rec=new MRec{
                    token=tok, scope=scope, dir=dir,
                    ilSize=JInt(mi,"ilSize"), ilSha=JS(mi,"ilSha256"), maxStack=JInt(mi,"maxStack"),
                    ehCount=JInt(mi,"ehCount"), placeholder=JB(mi,"placeholderSuspected"),
                    name=JS(id,"name"), declNs=declNs, declName=declName, dynamicOnly=JB(id,"dynamicOnly"),
                };
                try { locDoc=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"locals.json"))); } catch { }
                if(locDoc!=null) using(locDoc){ rec.localsCount=JInt(locDoc.RootElement,"count"); rec.localsCbSig=JInt(locDoc.RootElement,"cbSig"); }
                return rec;
            }
        }
    }
    static List<EhMeta> ReadEh(string dir){
        var m=new Dictionary<int,EhMeta>();
        var p=Path.Combine(dir,"eh.jsonl"); if(!File.Exists(p)) return new List<EhMeta>();
        foreach(var line in File.ReadLines(p)){
            var s=line.Trim(); if(s.Length==0) continue;
            try { using var d=JsonDocument.Parse(s); var o=d.RootElement; int n=JInt(o,"ehNumber");
                if(!m.ContainsKey(n)) m[n]=new EhMeta{ ehNumber=n, flags=JInt(o,"flags"),
                    tryOffset=JInt(o,"tryOffset"), tryLength=JInt(o,"tryLength"),
                    handlerOffset=JInt(o,"handlerOffset"), handlerLength=JInt(o,"handlerLength"),
                    classTokenOrFilter=JS(o,"classTokenOrFilter") }; } catch { }
        }
        return m.Keys.OrderBy(k=>k).Select(k=>m[k]).ToList();
    }

    class Counters { public int dirs, noIdentity, skippedFilter, placeholder, dynamic; }

    // ===== DRAG & DROP (v1.2): keo protected exe vao -> detect dump trong .\Dumps\ -> full pipeline =====
    static int RunDragDrop(string dropped){
        Banner("DNGuard Unpacker — DRAG & DROP");
        dropped=Path.GetFullPath(dropped);
        Console.WriteLine("  file keo vao : "+dropped);
        string dir =Path.GetDirectoryName(dropped);
        string name=Path.GetFileName(dropped);
        string baseName=Path.GetFileNameWithoutExtension(name);

        // 1) tim DUMP: .\Dumps\<same-name>, else .\Dumps\<same-base>.{exe,dll}, else neu chinh file nam trong
        //    thu muc "Dumps" thi coi no la dump (nguoi dung keo nham dump) va tim protected o cap tren.
        string host=dropped, dump=null;
        string dumpsDir=Path.Combine(dir,"Dumps");
        if(File.Exists(Path.Combine(dumpsDir,name))) dump=Path.Combine(dumpsDir,name);
        else if(Directory.Exists(dumpsDir)){
            dump=Directory.GetFiles(dumpsDir).FirstOrDefault(f=>{
                var n=Path.GetFileNameWithoutExtension(f);
                var e=Path.GetExtension(f).ToLowerInvariant();
                return string.Equals(n,baseName,StringComparison.OrdinalIgnoreCase) && (e==".exe"||e==".dll"); });
        }
        if(dump==null && string.Equals(Path.GetFileName(dir),"Dumps",StringComparison.OrdinalIgnoreCase)){
            // keo nham file dump: dump=chinh no, host=file cung ten o thu muc cha
            dump=dropped; var parent=Path.GetDirectoryName(dir);
            var h=Path.Combine(parent,name); host=File.Exists(h)?h:dropped;
        }
        if(dump==null || !File.Exists(dump)){
            Console.WriteLine();
            Console.WriteLine("  [!] KHONG tim thay DUMP.");
            Console.WriteLine("      Hay dump module (ExtremeDumper) vao:  "+dumpsDir+"\\"+name);
            Console.WriteLine("      roi keo lai file protected nay vao tool.");
            if(Directory.Exists(dumpsDir)){ Console.WriteLine("      \\Dumps\\ hien co:");
                foreach(var f in Directory.GetFiles(dumpsDir)) Console.WriteLine("        "+Path.GetFileName(f)); }
            else Console.WriteLine("      (chua co thu muc \\Dumps\\)");
            Pause(); return 3;
        }
        Console.WriteLine("  dump detect  : "+dump);
        Console.WriteLine("  host (run)   : "+host);

        // 2) target-ns = ten module (root namespace thuong trung ten assembly)
        string ns=baseName;
        // 3) output mac dinh: <dir>\_dgunpack\
        string work=Path.Combine(dir,"_dgunpack");
        try { Directory.CreateDirectory(work); } catch { }
        string corpusOut=Path.Combine(work,"corpus");
        string outDll=Path.Combine(work, baseName+".rebuilt.dll");
        Console.WriteLine("  corpus-out   : "+corpusOut);
        Console.WriteLine("  out          : "+outDll);

        var a=new List<string>{ "--all", "--host", host, "--module", dump,
            "--corpus-out", corpusOut, "--out", outDll, "--target-ns", ns, "--verbose", "--strings" };
        if(Environment.GetEnvironmentVariable("DGUNPACK_DRYRUN")=="1") a.Add("--dry-run");
        int rc;
        try { rc=RunAll(a.ToArray()); }
        catch(Exception ex){ Console.Error.WriteLine("PIPELINE LOI: "+ex.Message); rc=9; }
        Console.WriteLine();
        Console.WriteLine(rc==0 ? "  ===> XONG. Mo "+outDll+" trong dnSpy de doc code." : "  ===> Loi (rc="+rc+"). Xem log tren.");
        Pause(); return rc;
    }
    static void Pause(){ try { Console.WriteLine(); Console.Write("Nhan phim bat ky de dong cua so..."); Console.ReadKey(true); Console.WriteLine(); } catch { } }

    // resolve a helper tool: explicit --arg wins; else prefer a copy BESIDE this exe (self-contained
    // package folder); else the given scattered-layout fallback.
    static string ResolveTool(string argVal, string fileName, string fallback){
        if(!string.IsNullOrEmpty(argVal)) return argVal;
        string beside=Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(beside) ? beside : fallback;
    }

    // ===== ALL-IN-ONE (v1.1): capture -> index -> rebuild, one command =========================
    static int RunAll(string[] args){
        string host      = Arg(args,"--host");        // protected exe to RUN (live, HVM active)
        string modulePath= Arg(args,"--module");      // dumped exe (metadata to splice into)
        string outPath   = Arg(args,"--out");
        string corpusOut = Arg(args,"--corpus-out");  // where capture writes the corpus
        if(host==null||modulePath==null||outPath==null||corpusOut==null){
            Console.Error.WriteLine("usage: DNGuardRebuilder --all --host <protected.exe> --module <dump.exe> --corpus-out <rc-dir> --out <rebuilt.dll>\n" +
                "        [--tools-root C:\\tool_dng] [--launcher <exe>] [--shim <dll>] [--forcejit <dll>]\n" +
                "        [--warmup 30000] [--passes 2] [--target-ns NS] [--only-types A B] [--verbose] [--skip-capture]");
            return 2;
        }
        string root     = Arg(args,"--tools-root", @"C:\tool_dng");
        // resolve each helper: explicit arg > BESIDE this exe (self-contained package) > scattered layout.
        string launcher = ResolveTool(Arg(args,"--launcher"), "DNGuardJitLauncher.exe", Path.Combine(root, @"DNGuardJitShim_v0.3k\DNGuardJitShim\DNGuardJitLauncher.exe"));
        string shim     = ResolveTool(Arg(args,"--shim"),     "DNGuardJitShim.dll",     Path.Combine(root, @"DNGuardJitShim_v0.3k\DNGuardJitShim\DNGuardJitShim.dll"));
        string forcejit = ResolveTool(Arg(args,"--forcejit"), "ForceJit.dll",           Path.Combine(root, @"ForceJit\bin\Release\net8.0\ForceJit.dll"));
        bool skipCapture= HasFlag(args,"--skip-capture");

        bool dryRun = HasFlag(args,"--dry-run");
        Banner("DNGuard Unpacker — ALL-IN-ONE (capture → index → rebuild)");
        Console.WriteLine($"  host (run) : {host}");
        Console.WriteLine($"  module     : {modulePath}");
        Console.WriteLine($"  corpus-out : {corpusOut}");
        Console.WriteLine($"  out        : {outPath}");
        Console.WriteLine($"  launcher   : {launcher}");
        Console.WriteLine($"  shim       : {shim}");
        Console.WriteLine($"  forcejit   : {forcejit}");
        if(dryRun){ Console.WriteLine("\n  [DRY-RUN] chi in ke hoach, KHONG launch/rebuild."); return 0; }

        if(!skipCapture){
            // read the target MVID straight from the dump so ForceJit targets the right module.
            string mvid;
            try { using var mm=ModuleDefMD.Load(modulePath); mvid=mm.Mvid?.ToString(); }
            catch(Exception ex){ Console.Error.WriteLine("cannot read module MVID: "+ex.Message); return 3; }

            foreach(var need in new[]{launcher,shim,forcejit})
                if(!File.Exists(need)){ Console.Error.WriteLine("missing tool: "+need+"  (pass --launcher/--shim/--forcejit or --tools-root)"); return 3; }

            Phase("STEP 1/2  Capture — launch protected app under shim + ForceJit sweep (auto-exit)");
            Console.WriteLine($"      launcher={launcher}");
            Console.WriteLine($"      MVID={mvid}  warmup={Arg(args,"--warmup","30000")}ms passes={Arg(args,"--passes","2")}");
            var psi=new ProcessStartInfo(launcher){ UseShellExecute=false, WorkingDirectory=Path.GetDirectoryName(launcher) };
            psi.ArgumentList.Add("--host"); psi.ArgumentList.Add(host);
            psi.ArgumentList.Add("--shim"); psi.ArgumentList.Add(shim);
            psi.ArgumentList.Add("--out");  psi.ArgumentList.Add(corpusOut);
            psi.ArgumentList.Add("--mode"); psi.ArgumentList.Add("clrjit-direct");
            // env goes to the launcher, which passes it to the host (ForceJit runs INSIDE the protected app).
            psi.Environment["DOTNET_STARTUP_HOOKS"]   = forcejit;
            psi.Environment["DG_FORCEJIT_MVID"]       = mvid;
            psi.Environment["DG_FORCEJIT_DRY"]        = "0";
            psi.Environment["DG_FORCEJIT_GENERICS"]   = "1";
            psi.Environment["DG_FORCEJIT_AUTOEXIT"]   = "1";     // close the app itself after SWEEP COMPLETE
            psi.Environment["DG_FORCEJIT_WARMUP_MS"]  = Arg(args,"--warmup","30000");
            psi.Environment["DG_FORCEJIT_PASSES"]     = Arg(args,"--passes","2");
            try {
                var p=Process.Start(psi); p.WaitForExit();
                Console.WriteLine($"      capture finished (launcher exit={p.ExitCode}); corpus at {corpusOut}");
            } catch(Exception ex){ Console.Error.WriteLine("capture failed to start launcher: "+ex.Message); return 4; }
            if(!Directory.Exists(Path.Combine(corpusOut,"methods"))){
                Console.Error.WriteLine("      WARN: corpus has no methods/ dir — capture may have produced nothing.");
            }
        } else {
            Console.WriteLine("  (--skip-capture: reusing existing corpus at "+corpusOut+")");
        }

        // STEP 2: hand off to the unified index+rebuild pipeline on the freshly-captured corpus.
        // --host is passed through so the optional --strings step can re-run the app to decrypt strings.
        var rb=new List<string>{ "--corpus", corpusOut, "--module", modulePath, "--out", outPath, "--host", host };
        void Pass(string k){ var v=Arg(args,k,null); if(v!=null){ rb.Add(k); rb.Add(v); } }
        Pass("--target-ns"); Pass("--prologue-mode"); Pass("--eh-mode"); Pass("--resolve-refs"); Pass("--dep-dir");
        Pass("--strhook"); Pass("--str-timeout"); Pass("--tools-root");
        var ot=ArgList(args,"--only-types"); if(ot.Count>0){ rb.Add("--only-types"); rb.AddRange(ot); }
        if(HasFlag(args,"--verbose")) rb.Add("--verbose");
        if(HasFlag(args,"--quiet"))   rb.Add("--quiet");
        if(HasFlag(args,"--strings")) rb.Add("--strings");
        return RunPipeline(rb.ToArray());
    }

    // ===== the unified run ====================================================================
    static int RunPipeline(string[] args){
        var corpora     = ArgList(args,"--corpus");
        string modulePath = Arg(args,"--module");
        string outPath    = Arg(args,"--out");
        string targetNs   = Arg(args,"--target-ns","LordsMobileBot");
        var onlyTypesL    = ArgList(args,"--only-types");
        var onlyTypes     = onlyTypesL.Count>0 ? new HashSet<string>(onlyTypesL) : null;
        string modeStr    = (Arg(args,"--prologue-mode","strip") ?? "strip").ToLowerInvariant();
        PrologueMode pmode = modeStr=="strip"?PrologueMode.Strip:modeStr=="off"?PrologueMode.Off:PrologueMode.Report;
        bool ehFlatten    = string.Equals(Arg(args,"--eh-mode","flatten"),"flatten",StringComparison.OrdinalIgnoreCase);
        bool resolveRefs  = !string.Equals(Arg(args,"--resolve-refs","on"),"off",StringComparison.OrdinalIgnoreCase);
        string fieldMode  = (Arg(args,"--retarget-object-fields","high-confidence")??"high-confidence").ToLowerInvariant();
        bool retargetObjectFields = fieldMode!="off";
        string depDir     = Arg(args,"--dep-dir");
        bool verbose      = HasFlag(args,"--verbose");
        bool quiet        = HasFlag(args,"--quiet");   // suppress per-method line, keep phase + summary
        if(modulePath==null || outPath==null || corpora.Count==0){
            Console.Error.WriteLine("usage: DNGuardRebuilder --corpus <dir...> --module <dll> --out <dll> [--target-ns NS] [--only-types A B] [--prologue-mode strip] [--eh-mode flatten] [--resolve-refs on|off] [--dep-dir <dir>] [--verbose]");
            return 2;
        }
        if(depDir==null) depDir=Path.GetDirectoryName(Path.GetFullPath(modulePath));
        var sw=System.Diagnostics.Stopwatch.StartNew();

        Banner("DNGuard Unpacker — unified pipeline (corpus merge → IL restore)");
        Console.WriteLine($"  corpora    : {string.Join("  ", corpora)}");
        Console.WriteLine($"  module     : {modulePath}");
        Console.WriteLine($"  out        : {outPath}");
        Console.WriteLine($"  target-ns  : {targetNs}   only-types: {(onlyTypes==null?"(all)":string.Join(",",onlyTypes))}");
        Console.WriteLine($"  prologue={pmode}  eh-flatten={ehFlatten}  resolve-refs={resolveRefs}  retarget-fields={fieldMode}  verbose={verbose}");

        // ---- load module + resolver ----
        Phase("1/5  Load module + assembly resolver");
        ModuleDefMD module;
        try {
            var modCtx=ModuleDef.CreateModuleContext();
            if(resolveRefs && modCtx.AssemblyResolver is AssemblyResolver ar){
                ar.EnableTypeDefCache=true; ar.UseGAC=false; ar.PreSearchPaths.Insert(0, depDir);
            }
            module=ModuleDefMD.Load(modulePath, modCtx);
        } catch(Exception ex){ Console.Error.WriteLine("dnlib load failed: "+ex.Message); return 3; }
        LogOK($"module loaded: {module.Name}  MVID={module.Mvid}  dep-dir={depDir}");

        // ---- PASS 0: scan corpora, group by token (target scope) ----
        Phase("2/5  Scan corpus → pick target module scope");
        var ctr=new Counters();
        var byKey=new Dictionary<(string,uint),List<MRec>>();
        var scopeNs=new Dictionary<string,Dictionary<string,int>>();
        var scopeCount=new Dictionary<string,int>();
        foreach(var corpus in corpora){
            var methodsRoot=Path.Combine(corpus,"methods");
            if(!Directory.Exists(methodsRoot)){ Console.WriteLine($"      WARN: no methods dir in {corpus}"); continue; }
            int localDirs=0;
            foreach(var dir in Directory.EnumerateDirectories(methodsRoot)){
                ctr.dirs++; localDirs++;
                if((localDirs & 0x3FFF)==0) Console.Write($"\r      scanning… {ctr.dirs} dirs");
                var rec=ReadRec(dir, onlyTypes, ctr); if(rec==null) continue;
                var key=(rec.scope, rec.token);
                if(!byKey.TryGetValue(key,out var lst)){ lst=new List<MRec>(); byKey[key]=lst; } lst.Add(rec);
                var ns=rec.declNs??"";
                if(!scopeNs.TryGetValue(rec.scope,out var nm)){ nm=new Dictionary<string,int>(); scopeNs[rec.scope]=nm; }
                nm[ns]=nm.GetValueOrDefault(ns)+1;
                scopeCount[rec.scope]=scopeCount.GetValueOrDefault(rec.scope)+1;
            }
        }
        Console.Write("\r");
        string target=null; { string tn=targetNs.ToLowerInvariant(); long best=-1;
            foreach(var s in scopeCount.Keys){
                long score=scopeNs[s].Where(kv=>(kv.Key??"").ToLowerInvariant().Contains(tn)).Sum(kv=>(long)kv.Value);
                long rank=score*1000000L+scopeCount[s];
                if(rank>best){ best=rank; target=s; }
            }
        }
        var tgtByTok=new Dictionary<uint,List<MRec>>();
        foreach(var kv in byKey){ if(kv.Key.Item1!=target) continue;
            if(!tgtByTok.TryGetValue(kv.Key.Item2,out var l)){ l=new List<MRec>(); tgtByTok[kv.Key.Item2]=l; } l.AddRange(kv.Value); }
        LogOK($"target scope locked: {target}  ({tgtByTok.Count} methods, dynamicOnly={ctr.dynamic}, placeholder={ctr.placeholder})");

        // ---- PASS 1: callbacks → global handle map + per-token callbacks ----
        Phase("3/5  Build token maps (global handle union across all methods)");
        var cbsByTok=new Dictionary<uint,List<CB>>(tgtByTok.Count);
        var gConflict=new HashSet<string>(); var gReal=new Dictionary<string,uint>();
        int done=0, totalCb=0;
        foreach(var kv in tgtByTok){
            var cbs=new List<CB>();
            foreach(var rec in kv.Value) cbs.AddRange(ReadCallbacks(Path.Combine(rec.dir,"callbacks.jsonl")));
            cbsByTok[kv.Key]=cbs; totalCb+=cbs.Count;
            foreach(var c in cbs){ var h=c.Handle(); if(h=="0x0") continue; var r=c.RealToken(); if(r==0) continue;
                if(gReal.TryGetValue(h,out var ex)){ if(ex!=r) gConflict.Add(h); } else gReal[h]=r; }
            if((++done & 0xFFF)==0) Console.Write($"\r      reading callbacks… {done}/{tgtByTok.Count}");
        }
        foreach(var h in gConflict) gReal.Remove(h);
        Console.Write("\r");
        LogOK($"callbacks ingested: {totalCb} events  globalHandleReal={gReal.Count}  conflictsDropped={gConflict.Count}");

        // ---- shared rebuild state ----
        var importer=new Importer(module, ImporterOptions.TryToUseTypeDefs);
        var importCache=new Dictionary<string,IMemberRef>(); var typeCache=new Dictionary<string,TypeDef>();
        var st=new RunStats();

        // ---- PASS 2: per-method rebuild (rich logging) ----
        Phase("4/5  Restore IL (translate virtual tokens → resolve refs → infer types)");
        LogInfo("mode: prologue=" + pmode + "  eh-flatten=" + ehFlatten + "  resolve-refs=" + resolveRefs + "  retarget-fields=" + fieldMode);
        var rebuiltToks=new List<uint>();
        var rebuiltList=new List<(uint tok,int unmapped,string name)>();
        int idx=0; int nTok=tgtByTok.Count;
        foreach(var tok in tgtByTok.Keys.OrderBy(x=>x)){
            idx++;
            var recs=tgtByTok[tok];
            var good=recs.Where(r=>!r.placeholder && r.ilSize>0).ToList(); if(good.Count==0) good=recs;
            var best=good.Aggregate((a,b)=>b.ilSize>a.ilSize?b:a);
            string ilPath=Path.Combine(best.dir,"generated-il.bin");   // corpus IL file name (index renames to il.bin)
            if(!File.Exists(ilPath)){ st.Skip("no-il"); continue; }

            var method=module.ResolveToken(tok) as MethodDef;
            if(method==null){ st.Skip("resolve-failed"); continue; }
            if(method.IsAbstract||method.IsPinvokeImpl){ st.Skip("abstract-or-pinvoke"); continue; }

            var cbs=cbsByTok.TryGetValue(tok,out var cc)?cc:new List<CB>();
            var vmap=BuildTokenmapUnion(cbs, gReal);   // tokenmap always needed for PatchTokens
            var hints=resolveRefs ? BuildHints(cbs, vmap) : new Dictionary<uint,Hint>();

            // eh + locals meta from the best rec
            var meta=new Meta{ name=best.name, ilSize=best.ilSize, maxStack=best.maxStack, ehCount=Math.Max(0,recs.Max(r=>r.ehCount)) };
            meta.eh=ReadEh(best.dir);
            int locCount=recs.Where(r=>r.localsCount>0).Select(r=>r.localsCount).DefaultIfEmpty(0).Max();
            if(locCount>0) meta.locals=new LocalsMeta{ count=locCount };
            // locals blob: prefer a rec that has locals.bin + cbSig>0
            string locBin=null; var blobRec=recs.FirstOrDefault(r=>r.localsCbSig>0 && File.Exists(Path.Combine(r.dir,"locals.bin")));
            if(blobRec!=null){ locBin=Path.Combine(blobRec.dir,"locals.bin"); meta.hasLocalsBlob=true; }

            var res=RebuildOne(module, importer, importCache, typeCache, method, tok, File.ReadAllBytes(ilPath),
                               meta, vmap, hints, locBin, pmode, ehFlatten, resolveRefs, st);
            if(res==null){ continue; } // skipped (counted in st)
            rebuiltToks.Add(tok);
            rebuiltList.Add((tok, res.Value.effUnmapped, best.name));

            if(!quiet && (verbose || (idx & 0x3FF)==0)){
                string nm=(best.declName!=null?best.declName+"::":"")+(best.name??"");
                string line=$"  [{idx,6}/{nTok}] 0x{tok:X8} {Trunc(nm,42),-42} IL={best.ilSize,4}B  patch={res.Value.patched,-4} ref={res.Value.refFixed,-4} infer={res.Value.inferred,-4} unmap={res.Value.effUnmapped,-3} {(res.Value.effUnmapped==0?"OK":"~")}";
                if(verbose) Console.WriteLine(line); else Console.Write("\r"+line.PadRight(110));
            }
        }
        if(!verbose && !quiet) Console.Write("\r".PadRight(112)+"\r");

        FieldRetargetResult fieldRetargetResult=null;
        int postFieldLocalRefined=0,postFieldObjectRefined=0,postFieldInitobjFixed=0,postFieldMethodsChanged=0;
        if(retargetObjectFields){
            string fieldReportDir=Path.GetDirectoryName(Path.GetFullPath(outPath));
            fieldRetargetResult=RetargetObjectFieldsHighConfidence(module,fieldReportDir);
            if(fieldRetargetResult.Retargeted>0){
                postFieldMethodsChanged=RefineAfterFieldRetarget(module,rebuiltToks,
                    ref postFieldLocalRefined,ref postFieldObjectRefined,ref postFieldInitobjFixed,
                    st.objectTypeRefinedByOpcode);
                st.localTypesRefined+=postFieldLocalRefined;
                st.objectTypeOperandsRefined+=postFieldObjectRefined;
                st.initobjFixed+=postFieldInitobjFixed;
            }
        }

        LogOK($"IL restored on {rebuiltToks.Count} methods; virtual tokens patched={st.totPatched}, unmapped(raw)={st.totUnmapped}");
        if(resolveRefs){
            LogOK($"external-ref: fixed={st.refsResolved} (methods={st.refsMethodsFixed}, fields={st.refsFieldsFixed}; approx-generic={st.refsApprox}); still-unresolved={st.refsUnresolved}");
            LogInfo($"type-inference: inferred={st.refsInferred} (object-fallback={st.refsInferFallback})");
        }
        LogInfo($"prologue: stripped={st.prologueStripped}  locals-inferred={st.localsInferred}  eh-flattened={st.ehFlattened}");
        LogInfo($"local-rebind: operands={st.localOperandsRebound} methods={st.localOperandMethods} raw-count-recovered={st.rawLocalRecovered}");
        LogInfo($"semantic-locals(v0.8.0): refined={st.localTypesRefined} pass-events={st.localTypeMethods} Boolean={st.booleanLocalsRefined}");
        LogInfo($"residual-open-generic-closed={st.residualGenericLocalsFixed}");
        if(fieldRetargetResult!=null){
            LogOK($"object-fields-retargeted={fieldRetargetResult.Retargeted} arrays={fieldRetargetResult.ArrayRetargeted} conflicts={fieldRetargetResult.Conflicted} weak-only={fieldRetargetResult.WeakOnly}");
            LogInfo($"post-field-refinement: methods={postFieldMethodsChanged} locals={postFieldLocalRefined} type-operands={postFieldObjectRefined} initobj={postFieldInitobjFixed}");
        }
        LogInfo($"object-array/address-refined={st.objectTypeOperandsRefined}");
        LogInfo($"exact-initobj={st.initobjFixed}  dead-invalid-nopped={st.deadInvalidNopped} post-parse={st.parsedDeadNopped}");
        if(st.skip.Count>0) LogWarn("skipped: "+string.Join("  ", st.skip.OrderByDescending(k=>k.Value).Select(k=>$"{k.Key}={k.Value}")));
        int fully=rebuiltList.Count(r=>r.unmapped==0);
        LogOK($"fully-token-translated (unmapped=0): {fully}/{rebuiltList.Count}");

        // rebuilt.txt
        try {
            string outDir=Path.GetDirectoryName(Path.GetFullPath(outPath));
            var lines=new List<string>{ "# token\tunmapped\tname  (unmapped=0 = token-translated only; see semantic-validation.txt)" };
            lines.AddRange(rebuiltList.OrderBy(r=>r.unmapped).Select(r=>$"0x{r.tok:X8}\t{r.unmapped}\t{r.name}"));
            File.WriteAllLines(Path.Combine(outDir,"rebuilt.txt"), lines);
        } catch { }

        // ---- write ----
        Phase("5/5  Write restored assembly");
        try {
            var opts=new ModuleWriterOptions(module); opts.MetadataOptions.Flags|=MetadataFlags.PreserveAll;
            opts.Logger=DummyLogger.NoThrowInstance; module.Write(outPath, opts);
        } catch(Exception ex){ Console.Error.WriteLine("write failed: "+ex.Message); return 4; }
        LogOK($"assembly written: {outPath}");
        try {
            var validation=ValidateWrittenModule(outPath,rebuiltToks);
            LogOK($"post-write validator: structural={validation.StructurallyValid}/{validation.Methods} core-clean={validation.SemanticCoreClean} strict-clean={validation.SemanticStrictClean}");
            LogInfo($"validator issues: invalid={validation.InvalidMetadataOperands} stubs={validation.DnGuardStubs} open-generic={validation.OpenGenericLocals} initobj={validation.InitobjMismatches} object-fallback={validation.ObjectFallbackOperands}");
        } catch(Exception ex){ LogWarn("semantic validator failed: "+ex.Message); }

        // v1.3 STRING DECRYPT (optional --strings): scan accessors -> run host to decrypt -> inline ldstr.
        if(HasFlag(args,"--strings"))
            RunStringDecrypt(outPath, Arg(args,"--host"), module.Mvid?.ToString(),
                ResolveTool(Arg(args,"--strhook"), "StrDumpHook.dll",
                    Path.Combine(Arg(args,"--tools-root",@"C:\tool_dng"), @"DNGuardStringDecrypt\DNGuardStringDecrypt\StrDumpHook\StrDumpHook.dll")),
                HasFlag(args,"--verbose"), Arg(args,"--str-timeout","180"));
        sw.Stop();
        Banner($"DONE  —  {rebuiltToks.Count} methods, {fully} fully-token-translated  ({sw.Elapsed.TotalSeconds:F0}s)");
        return 0;
    }

    static int ScanStringsDebug(string dll){
        if(!File.Exists(dll)){ Console.Error.WriteLine("khong thay: "+dll); return 2; }
        using var mod=ModuleDefMD.Load(dll);
        var accs=ScanAccessors(mod, "CheckString", "ZYXDNGuarder");
        Console.WriteLine($"[scan] {mod.Name}: {accs.Count} accessor goi ZYXDNGuarder.CheckString");
        for(int i=0;i<accs.Count && i<12;i++) Console.WriteLine($"    0x{accs[i].token:X8}  id={accs[i].id} len={accs[i].len}");
        return 0;
    }

    // ===== STRING DECRYPT (v1.3): scan accessor -> runtime dump -> inline (port StrTool + orchestrate) ===
    struct Acc { public uint token; public int id, len; }
    static List<Acc> ScanAccessors(ModuleDefMD mod, string checkName, string rtType){
        var outp=new List<Acc>();
        foreach(var t in mod.GetTypes()) foreach(var m in t.Methods){
            if(!m.IsStatic || m.Parameters.Count!=0 || !m.HasBody) continue;
            var ins=m.Body.Instructions; int id=0,len=0; bool calls=false;
            for(int i=0;i<ins.Count;i++){
                if(ins[i].OpCode.Code==Code.Call && ins[i].Operand is IMethod im && im.Name==checkName
                   && (rtType=="*" || (im.DeclaringType?.FullName?.IndexOf(rtType, StringComparison.OrdinalIgnoreCase) ?? -1)>=0)){
                    calls=true; var consts=new List<int>();
                    for(int j=i-1;j>=0 && consts.Count<2;j--){ if(TryLdc(ins[j], out int v)) consts.Insert(0,v); else if(ins[j].OpCode.Code!=Code.Nop) break; }
                    if(consts.Count>=1) id=consts[0];
                    if(consts.Count>=2) len=consts[1];
                    break;
                }
            }
            if(calls) outp.Add(new Acc{ token=m.MDToken.Raw, id=id, len=len });
        }
        return outp;
    }
    static int InlineStrings(ModuleDefMD mod, Dictionary<uint,string> map, bool verbose){
        int rep=0;
        foreach(var t in mod.GetTypes()) foreach(var m in t.Methods){
            if(!m.HasBody) continue;
            foreach(var instr in m.Body.Instructions){
                if((instr.OpCode.Code==Code.Call||instr.OpCode.Code==Code.Callvirt) && instr.Operand is MethodDef md
                   && map.TryGetValue(md.MDToken.Raw, out var s)){
                    instr.OpCode=OpCodes.Ldstr; instr.Operand=s; rep++;
                }
            }
        }
        return rep;
    }

    // inline one method at a time with per-method logging
    static int InlineStringVerbose(MethodDef m, Dictionary<uint,string> map, ref int rep, bool verbose){
        int local=0;
        if(!m.HasBody) return 0;
        var changes=new List<(Instruction ins,string s)>();
        foreach(var instr in m.Body.Instructions){
            if((instr.OpCode.Code==Code.Call||instr.OpCode.Code==Code.Callvirt) && instr.Operand is MethodDef md
               && map.TryGetValue(md.MDToken.Raw, out var s)){
                changes.Add((instr,s));
            }
        }
        foreach(var (ins,s) in changes){
            ins.OpCode=OpCodes.Ldstr; ins.Operand=s; rep++; local++;
        }
        if(verbose && local>0){
            var preview = changes.Count>0 ? Trunc((changes[0].s??"").Replace("\n","\\n").Replace("\r",""),50) : "";
            Console.WriteLine($"        ↳ {TypeName(m.DeclaringType),-40} :: {m.Name,-25} {local,3}x  e.g. \"{preview}\"");
        }
        return local;
    }
    static bool TryLdc(Instruction ins, out int val){
        val=0;
        switch(ins.OpCode.Code){
            case Code.Ldc_I4: val=(int)ins.Operand; return true;
            case Code.Ldc_I4_S: val=(sbyte)ins.Operand; return true;
            case Code.Ldc_I4_0: val=0; return true;   case Code.Ldc_I4_1: val=1; return true;
            case Code.Ldc_I4_2: val=2; return true;   case Code.Ldc_I4_3: val=3; return true;
            case Code.Ldc_I4_4: val=4; return true;   case Code.Ldc_I4_5: val=5; return true;
            case Code.Ldc_I4_6: val=6; return true;   case Code.Ldc_I4_7: val=7; return true;
            case Code.Ldc_I4_8: val=8; return true;   case Code.Ldc_I4_M1: val=-1; return true;
            default: return false;
        }
    }
    static int RunStringDecrypt(string dllPath, string hostPath, string mvid, string strhook, bool verbose, string timeoutSec, string outputOverride=null){
        Phase("STRING DECRYPT — scan accessor stubs → runtime dump → inline ldstr");
        if(string.IsNullOrEmpty(hostPath) || !File.Exists(hostPath)){
            LogWarn("--strings needs --host <protected exe> to decrypt runtime strings. Skipping."); return 3; }
        if(!File.Exists(strhook)){ LogWarn("StrDumpHook.dll not found: "+strhook+" -> skipping."); return 3; }
        string work=Path.GetDirectoryName(Path.GetFullPath(dllPath));
        string reqPath=Path.Combine(work,"_str_requests.json"), strPath=Path.Combine(work,"_str_strings.json");

        // 1) SCAN accessor stubs on the rebuilt DLL
        LogInfo("stage 1/3: scanning accessor stubs calling ZYXDNGuarder.CheckString ...");
        int nAcc;
        List<Acc> accs;
        try {
            using var mod=ModuleDefMD.Load(dllPath);
            accs=ScanAccessors(mod, "CheckString", "ZYXDNGuarder");
            nAcc=accs.Count;
            var sb=new System.Text.StringBuilder("[\n");
            for(int i=0;i<accs.Count;i++){ var a=accs[i];
                sb.Append($"  {{\"token\":\"0x{a.token:X8}\",\"id\":{a.id},\"len\":{a.len}}}").Append(i<accs.Count-1?",\n":"\n"); }
            sb.Append("]\n"); File.WriteAllText(reqPath, sb.ToString());
        } catch(Exception ex){ LogWarn("scan failed: "+ex.Message); return 3; }
        LogOK($"stage 1/3: found {nAcc} accessor stubs");
        if(nAcc==0){ LogInfo("(0 accessors — module may already be inlined / has no string class). Skipping."); return 0; }
        if(verbose){
            int shown=0;
            foreach(var a in accs.OrderBy(x=>x.id).Take(8)){
                Console.WriteLine($"        accessor[{shown++,3}] 0x{a.token:X8}  id={a.id,5}  len={a.len,4}");
            }
            if(nAcc>8) Console.WriteLine($"        … {nAcc-8} more accessors (full list → _str_requests.json)");
        }

        // 2) DUMP: launch host with StrDumpHook (auto-exit) to invoke accessors -> strings.json
        long startTick=Environment.TickCount64;
        LogInfo($"stage 2/3: runtime string dump on protected host ({nAcc} strings to resolve, timeout={timeoutSec}s)");
        try { if(File.Exists(strPath)) File.Delete(strPath); } catch { }
        var psi=new ProcessStartInfo(hostPath){ UseShellExecute=false, WorkingDirectory=Path.GetDirectoryName(hostPath) };
        psi.Environment["DOTNET_STARTUP_HOOKS"]=strhook;
        psi.Environment["DG_STRDUMP_REQUESTS"]=reqPath;
        psi.Environment["DG_STRDUMP_OUT"]=strPath;
        if(!string.IsNullOrEmpty(mvid)) psi.Environment["DG_STRDUMP_MVID"]=mvid;
        psi.Environment["DG_STRDUMP_AUTOEXIT"]="1";
        psi.Environment["DG_STRDUMP_WARMUP_MS"]="30000";
        int tmo=int.TryParse(timeoutSec,out var ts)?ts:180;
        long lastSize=-1; int stablePolls=0;
        try {
            var p=Process.Start(psi);
            var deadline=DateTime.UtcNow.AddSeconds(tmo);
            while(!p.HasExited && DateTime.UtcNow<deadline){
                System.Threading.Thread.Sleep(900);
                if(File.Exists(strPath)){
                    long cur=new FileInfo(strPath).Length;
                    if(cur!=lastSize){ Console.Write($"\r        … strings.json : {cur:N0} bytes   "); stablePolls=0; lastSize=cur; }
                    else { stablePolls++; if(stablePolls>=3) break; }
                } else {
                    Console.Write($"\r        … host running (pid={p.Id}), waiting for hook output   ");
                }
            }
            if(!p.HasExited && DateTime.UtcNow>=deadline){
                Console.WriteLine();
                LogWarn($"dump: timeout {tmo}s -> killing host"); try{ p.Kill(true);}catch{}
            }
        } catch(Exception ex){ Console.WriteLine(); LogWarn("dump could not launch host: "+ex.Message); return 3; }
        Console.Write("\r".PadRight(90)+"\r");
        long dumpMs=Environment.TickCount64-startTick;
        if(!File.Exists(strPath)){
            LogWarn("no strings.json produced (dump failed / app needs login first). The rebuilt DLL is still usable, just without inlined strings."); return 3; }
        LogOK($"stage 2/3: strings dumped in {dumpMs/1000.0:F1}s → {new FileInfo(strPath).Length:N0} bytes");

        // 3) INLINE: replace accessor calls -> ldstr, write *.strings.dll
        LogInfo("stage 3/3: inlining decrypted strings as ldstr ...");
        try {
            var map=new Dictionary<uint,string>();
            using(var doc=JsonDocument.Parse(File.ReadAllText(strPath)))
                foreach(var pr in doc.RootElement.EnumerateObject())
                    if(TryTok(pr.Name, out var tk) && pr.Value.ValueKind==JsonValueKind.String) map[tk]=pr.Value.GetString();
            LogInfo($"     parsed {map.Count:N0} decrypted strings from hook output");

            using var mod=ModuleDefMD.Load(dllPath);
            int rep=0; int methodsTouched=0;
            var perType=new Dictionary<string,int>();
            var allTypes=mod.GetTypes().ToList();
            for(int ti=0;ti<allTypes.Count;ti++){
                var t=allTypes[ti];
                foreach(var m in t.Methods){
                    int before=rep;
                    rep+=InlineStringVerbose(m, map, ref rep, verbose);
                    if(rep>before){ methodsTouched++;
                        var tn=TypeName(t);
                        perType[tn]=perType.GetValueOrDefault(tn)+ (rep-before);
                    }
                }
                if((ti&0x3F)==0) Console.Write($"\r        … classes scanned: {ti+1}/{allTypes.Count}, methods touched: {methodsTouched}, ldstr written: {rep}   ");
            }
            Console.Write("\r".PadRight(110)+"\r");

            string outStr;
            if (!string.IsNullOrEmpty(outputOverride)) outStr = outputOverride;
            else {
                // keep source module kind: exe stays exe, dll stays dll
                var srcExt = Path.GetExtension(dllPath);
                var outExt = string.Equals(srcExt, ".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : ".dll";
                outStr = Path.Combine(work, Path.GetFileNameWithoutExtension(dllPath) + ".strings" + outExt);
            }
            var opts=new ModuleWriterOptions(mod); opts.MetadataOptions.Flags|=MetadataFlags.PreserveAll; opts.Logger=DummyLogger.NoThrowInstance;
            mod.Write(outStr, opts);

            if(verbose && perType.Count>0){
                LogInfo("     inline hotspots:");
                foreach(var kv in perType.OrderByDescending(k=>k.Value).Take(6))
                    Console.WriteLine($"        {kv.Value,4}x  {kv.Key}");
                if(perType.Count>6) Console.WriteLine($"        … and {perType.Count-6} more classes");
            }
            LogOK($"stage 3/3: {rep} accessor calls inlined → ldstr across {methodsTouched} methods ({map.Count:N0} strings)");
            LogOK($"output DLL with strings: {outStr}");
        } catch(Exception ex){ Console.WriteLine("      inline failed: "+ex.Message); return 3; }
        try { File.Delete(reqPath); } catch { }
        return 0;
    }

    // per-method rebuild result
    struct OneResult { public int patched, refFixed, inferred, effUnmapped; }

    // Rebuild ONE method into `method`. Mirrors Main's --index body but takes in-memory vmap/hints.
    // Returns null if the method was skipped (reason tallied into st.skip).
    static OneResult? RebuildOne(ModuleDefMD module, Importer importer, Dictionary<string,IMemberRef> importCache,
        Dictionary<string,TypeDef> typeCache, MethodDef method, uint tok, byte[] il, Meta meta,
        Dictionary<uint,uint> vmap, Dictionary<uint,Hint> hints, string locBin,
        PrologueMode pmode, bool ehFlatten, bool resolveRefs, RunStats st){

        bool ehMissing = meta.ehCount>0 && meta.eh.Count==0;
        if(ehMissing && !ehFlatten){ st.Skip("eh-missing"); return null; }
        try {
            if(pmode!=PrologueMode.Off && TryDetectPrologue(il, out int pEnd, out _)){
                int minEh=int.MaxValue; foreach(var e in meta.eh) minEh=Math.Min(minEh, Math.Min(e.tryOffset, e.handlerOffset));
                bool ehInside=meta.eh.Count>0 && minEh<pEnd;
                if(pmode==PrologueMode.Strip && !ehInside){ for(int k=0;k<pEnd;k++) il[k]=0x00; st.prologueStripped++; }
            }
            il=PatchTokens(il, vmap, out int tp, out int tu);
            st.totPatched+=tp; st.totUnmapped+=tu;
            st.deadInvalidNopped+=NopDeadInvalidTokenInstructions(il);
            var gp=GenericParamContext.Create(method);
            IList<Parameter> pars=new List<Parameter>(method.Parameters);
            CilBody body=MethodBodyReader.CreateCilBody(module, il, (byte[])null, pars,
                (ushort)0, (ushort)Math.Max(meta.maxStack,8), (uint)il.Length, 0u, gp);
            if(body.Instructions.Count==0){ st.Skip("parsed-empty"); return null; }

            int mResolved=0, mRefFixed=0, mInferred=0;
            if(resolveRefs){
                var byOff=new Dictionary<uint,Instruction>(); foreach(var ins in body.Instructions) byOff[ins.Offset]=ins;
                foreach(var (o,vt) in CollectVirtualOperands(il)){
                    byOff.TryGetValue((uint)o, out var ins);
                    IMemberRef mem=null; bool isField=false, approx=false;
                    if(hints.TryGetValue(vt, out var hh)) mem=ResolveRef(module, importer, importCache, typeCache, hh, out isField, out approx);
                    if(mem!=null && ins!=null){ ins.Operand=mem; st.refsResolved++; mResolved++; mRefFixed++;
                        if(isField) st.refsFieldsFixed++; else st.refsMethodsFixed++; if(approx) st.refsApprox++; }
                    else st.refsUnresolved++;
                }
            }
            // locals — v0.7.3 preserves raw numeric local indices before inference.
            if(!PrepareLocalsAndRebind(module,body,pars,gp,meta,il,locBin,null,
                out string localMode,out int rebound,out int rawRecovered,out string localError)){
                st.Skip(localError??"locals-prepare"); return null;
            }
            if(rebound>0){ st.localOperandsRebound+=rebound; st.localOperandMethods++; }
            st.rawLocalRecovered+=rawRecovered;
            if(localMode=="inferred" && body.Variables.Count>0) st.localsInferred++;
            int lref=RefineWeakLocalTypes(module,body,method,pars,out int bref);
            if(lref>0){ st.localTypesRefined+=lref; st.localTypeMethods++; st.booleanLocalsRefined+=bref; }
            st.initobjFixed+=RepairInitobjFromAddressProducer(body);
            // EH
            if(meta.eh.Count>0){
                var byOff=new Dictionary<uint,Instruction>(); foreach(var ins in body.Instructions) byOff[ins.Offset]=ins;
                Instruction At(long off){ return byOff.TryGetValue((uint)off, out var i)?i:null; }
                foreach(var c in meta.eh){
                    var eh=new ExceptionHandler((ExceptionHandlerType)c.flags);
                    eh.TryStart=At(c.tryOffset); eh.TryEnd=At(c.tryOffset+c.tryLength);
                    eh.HandlerStart=At(c.handlerOffset); eh.HandlerEnd=At(c.handlerOffset+c.handlerLength);
                    uint cf=ParseHex(c.classTokenOrFilter);
                    if((c.flags & 0x1)!=0) eh.FilterStart=At(cf);
                    else if(c.flags==0 && cf!=0){
                        if(IsVirtual(cf) && vmap.TryGetValue(cf,out uint realcf)) cf=realcf;
                        var ct=module.ResolveToken(cf) as ITypeDefOrRef;
                        eh.CatchType=ct ?? module.CorLibTypes.Object.ToTypeDefOrRef();
                    }
                    body.ExceptionHandlers.Add(eh);
                }
            }
            if(ehMissing && ehFlatten){ FlattenEH(body); st.ehFlattened++; }
            if(resolveRefs){
                int inf=InferTypeOperands(module, body, method, out int fb);
                st.refsInferred+=inf; st.refsInferFallback+=fb; st.refsUnresolved-=inf; mResolved+=inf; mInferred=inf;
            }
            int lref2=RefineWeakLocalTypes(module,body,method,pars,out int bref2);
            if(lref2>0){ st.localTypesRefined+=lref2; st.localTypeMethods++; st.booleanLocalsRefined+=bref2; }
            int rgfix=RepairResidualOpenGenericLocals(module,body,method,pars);
            if(rgfix>0){ st.residualGenericLocalsFixed+=rgfix; st.localTypesRefined+=rgfix; st.localTypeMethods++; }
            st.initobjFixed+=RepairInitobjFromAddressProducer(body);
            int objectFixed=RefineObjectFlowOperands(module,body,method,out var objectByOp);
            if(objectFixed>0){
                st.objectTypeOperandsRefined+=objectFixed; st.objectTypeOperandMethods++;
                foreach(var kv in objectByOp) st.objectTypeRefinedByOpcode[kv.Key]=st.objectTypeRefinedByOpcode.GetValueOrDefault(kv.Key)+kv.Value;
                int lref3=RefineWeakLocalTypes(module,body,method,pars,out int bref3);
                if(lref3>0){ st.localTypesRefined+=lref3; st.localTypeMethods++; st.booleanLocalsRefined+=bref3; }
                st.initobjFixed+=RepairInitobjFromAddressProducer(body);
            }
            int pdn=NopDeadUnresolvedInstructions(body);
            if(pdn>0){
                st.parsedDeadNopped+=pdn; st.deadInvalidNopped+=pdn; mResolved+=pdn;
                st.refsUnresolved=Math.Max(0,st.refsUnresolved-pdn);
            }
            body.MaxStack=(ushort)Math.Max(meta.maxStack,1); body.InitLocals=true;
            method.Body=body; method.ImplAttributes=MethodImplAttributes.IL|MethodImplAttributes.Managed;
            return new OneResult{ patched=tp, refFixed=mRefFixed, inferred=mInferred, effUnmapped=Math.Max(0,tu-mResolved) };
        } catch(Exception ex){ st.Skip("rebuild-exception:"+ex.GetType().Name); return null; }
    }

    // aggregate stats/counters shared across methods
    class RunStats {
        public int totPatched, totUnmapped, prologueStripped, localsInferred, ehFlattened;
        public int localOperandsRebound, localOperandMethods, rawLocalRecovered, initobjFixed, deadInvalidNopped;
        public int localTypesRefined, localTypeMethods, booleanLocalsRefined, parsedDeadNopped, residualGenericLocalsFixed;
        public int objectTypeOperandsRefined, objectTypeOperandMethods;
        public Dictionary<string,int> objectTypeRefinedByOpcode=new(StringComparer.OrdinalIgnoreCase);
        public int refsResolved, refsUnresolved, refsMethodsFixed, refsFieldsFixed, refsApprox, refsInferred, refsInferFallback;
        public Dictionary<string,int> skip=new();
        public void Skip(string w){ skip[w]=skip.GetValueOrDefault(w)+1; }
    }

    static string TypeName(TypeDef t){ if(t==null) return "?"; var ns=t.Namespace?.ToString(); return string.IsNullOrEmpty(ns) ? t.Name?.ToString() ?? "?" : ns+"."+(t.Name?.ToString() ?? "?"); }
    static string Trunc(string s, int n){ s??=""; return s.Length<=n?s:s.Substring(0,n-1)+"~"; }
    static void Banner(string s){ Console.WriteLine(); Console.WriteLine("=== "+s+" "+new string('=', Math.Max(0,72-s.Length))); }
    static void Phase(string s){ Console.WriteLine(); Console.WriteLine("-- [PHASE] "+s); }
    static void LogOK(string s){ Console.WriteLine("    [OK] "+s); }
    static void LogInfo(string s){ Console.WriteLine("    [*] "+s); }
    static void LogWarn(string s){ Console.WriteLine("    [!] "+s); }
}
