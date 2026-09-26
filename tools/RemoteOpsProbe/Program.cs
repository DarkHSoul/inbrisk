// Remote Operations feasibility probe — ISOLATED benchmark, not wired into
// production. Compares the inbox UIA remote-operation bytecode VM
// (Windows.UI.UIAutomation.Core, Win10 2104+/build 20348+) against the
// classic COM UIA path for: property batches, children traversal+props,
// full subtree DFS snapshot, and parent-chain ancestry.
//
// Usage: RemoteOpsProbe --pid <pid> | --hwnd <hex> | --title <substr>
//        [--reps N] [--ops caps,props,children,tree,ancestry]
//
// Read-only against the target window's UIA tree — never injects input.

using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using Windows.UI.UIAutomation.Core;

// ------------------------------------------------------------ args

int? pid = null; IntPtr hwnd = IntPtr.Zero; string? title = null; string? cls = null;
int reps = 15;
var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "caps", "props", "children", "tree", "ancestry" };

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pid": pid = int.Parse(args[++i]); break;
        case "--hwnd": hwnd = (IntPtr)Convert.ToInt64(args[++i], 16); break;
        case "--title": title = args[++i]; break;
        case "--class": cls = args[++i]; break;
        case "--reps": reps = int.Parse(args[++i]); break;
        case "--ops": ops = new(args[++i].Split(','), StringComparer.OrdinalIgnoreCase); break;
    }
}

if (hwnd == IntPtr.Zero)
{
    hwnd = FindWindowBy(pid, title, cls);
    if (hwnd == IntPtr.Zero)
    {
        Console.Error.WriteLine("no matching window — pass --pid/--hwnd/--title");
        return 2;
    }
}
GetWindowThreadProcessId(hwnd, out var tpid);
Console.WriteLine($"target hwnd=0x{hwnd.ToInt64():X} pid={(int)tpid} \"{WindowText(hwnd)}\"");

// ------------------------------------------------------------ UIA setup

IUIAutomation uia = (IUIAutomation)new CUIAutomation8Class();
var root = uia.ElementFromHandle(hwnd);
Console.WriteLine($"root name=\"{SafeStr(() => root.CurrentName)}\" class=\"{SafeStr(() => root.CurrentClassName)}\"");
Console.WriteLine($"os build={Environment.OSVersion.Version}");

// ------------------------------------------------------------ capability

static AutomationRemoteOperationOperandId OI(uint v) =>
    new() { Value = (int)v };

var probeOp = new CoreAutomationRemoteOperation();
probeOp.ImportElement(OI(1), ToAutoElement(root));

if (ops.Contains("caps"))
{
    var names = new (uint op, string n)[]
    {
        (0x39, "Navigate"), (0x38, "GetPropertyValue"),
        (0x06, "NewLoopBlock"), (0x07, "EndLoopBlock"),
        (0x08, "BreakLoop"), (0x09, "ContinueLoop"),
        (0x02, "ForkIfTrue"), (0x2A, "RemoteArrayAppend"),
        (0x2F, "RemoteStringMapInsert"), (0x50, "PopulateCache"),
        (0x1C, "Compare"), (0x3A, "IsNull"), (0x0E, "Add"), (0x0F, "Subtract"),
        (0x13, "BinarySubtract"), (0x2C, "RemoteArrayRemoveAt"), (0x2D, "RemoteArrayGetAt"),
        (0x2E, "RemoteArraySize"), (0x01, "Set"), (0x26, "NewStringMap"),
        (0x4C, "NewCacheRequest"), (0x4E, "CacheRequestAddProperty"),
    };
    Console.WriteLine("-- capability (provider process) --");
    foreach (var (o, n) in names)
    {
        try
        {
            Console.WriteLine($"  {n,-24} {(probeOp.IsOpcodeSupported(o) ? "supported" : "UNSUPPORTED")}");
        }
        catch (Exception e) { Console.WriteLine($"  {n,-24} ERROR {e.Message}"); }
    }
}

// ------------------------------------------------------------ offset convention probe
// The VM's relative-branch semantics aren't documented; determine them with
// a minimal counting loop: cnt=0; loop { if cnt==3 break; cnt+=1; cont }.

int offsetBase = -1;
foreach (var candidate in new[] { 1, 0 })
{
    var t = new Bc();
    var oCnt = t.Lit(); var o1 = t.Lit(); var o3 = t.Lit(); var oE = t.Lit();
    t.NewInt(oCnt, 0); t.NewInt(o1, 1); t.NewInt(o3, 3);
    int iLoop = t.Count;
    int pLoop = t.NewLoop(0, 0);
    int iBody = t.Count;
    t.Compare(oE, oCnt, o3, 0);
    int pFork = t.ForkIfTrue(oE, 0);
    t.Add(oCnt, o1);
    t.ContinueLoop();
    int iBrk = t.Count;
    t.BreakLoop();
    t.EndLoop();
    int iDone = t.Count;
    t.PatchRel(pLoop, 0, iDone - iLoop - candidate);
    t.PatchRel(pLoop, 1, iBody - iLoop - candidate);
    t.PatchRel(pFork, 0, iBrk - pFork - candidate);
    t.Emit(0x05); // Halt

    var p = new CoreAutomationRemoteOperation();
    p.ImportElement(OI(1), ToAutoElement(root));
    p.AddToResults(OI(oCnt));
    var res = p.Execute(t.Finish());
    if (res.Status == AutomationRemoteOperationStatus.Success)
    {
        offsetBase = candidate;
        Console.WriteLine($"offset convention target-(ip+{candidate}) works " +
            $"(cnt={res.GetOperand(OI(oCnt))})");
        break;
    }
    Console.WriteLine($"offset convention ip+{candidate}: {res.Status} @{res.ErrorLocation} hr=0x{res.ExtendedError:X8}");
}
if (offsetBase < 0)
{
    Console.WriteLine("!! remote-op VM unusable — both offset conventions failed");
    return 3;
}
int Rel(int fromInstr, int toInstr) => toInstr - fromInstr - offsetBase;

// ------------------------------------------------------------ bench helpers

var comTimes = new List<double>();
var ropTimes = new List<double>();
var ropFails = 0;
string? ropFailInfo = null;

static (double median, double p95, double max) Stats(List<double> v)
{
    v.Sort();
    return (v[v.Count / 2], v[(int)Math.Ceiling(v.Count * 0.95) - 1], v[^1]);
}

void Report(string name, string extra = "")
{
    var (m1, p1, x1) = Stats(comTimes); var (m2, p2, x2) = Stats(ropTimes);
    Console.WriteLine($"-- {name} {extra}");
    Console.WriteLine($"   COM : median={m1,7:F1}ms p95={p1,7:F1}ms max={x1,7:F1}ms");
    Console.WriteLine($"   REM : median={m2,7:F1}ms p95={p2,7:F1}ms max={x2,7:F1}ms");
    if (ropFails > 0) Console.WriteLine($"   REM FAILS: {ropFails} ({ropFailInfo})");
    comTimes.Clear(); ropTimes.Clear(); ropFails = 0; ropFailInfo = null;
}

double T(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }

// ------------------------------------------------------------ props: 60 reads

if (ops.Contains("props"))
{
    for (var r = 0; r < reps; r++)
    {
        comTimes.Add(T(() =>
        {
            for (var k = 0; k < 20; k++)
            {
                _ = root.GetCurrentPropertyValueEx(30005, 0);
                _ = root.GetCurrentPropertyValueEx(30003, 0);
                _ = root.GetCurrentPropertyValueEx(30012, 0);
            }
        }));

        ropTimes.Add(T(() =>
        {
            var b = new Bc();
            var rd = b.Lit();
            var pN = b.Lit(); var pC = b.Lit(); var pCl = b.Lit(); var ig = b.Lit();
            b.NewInt(pN, 30005); b.NewInt(pC, 30003); b.NewInt(pCl, 30012); b.NewBool(ig, false);
            for (var k = 0; k < 20; k++)
            {
                b.GetProp(rd, 1, pN, ig);
                b.GetProp(rd, 1, pC, ig);
                b.GetProp(rd, 1, pCl, ig);
            }
            b.Emit(0x05);
            var p = new CoreAutomationRemoteOperation();
            p.ImportElement(OI(1), ToAutoElement(root));
            var res = p.Execute(b.Finish());
            if (res.Status != AutomationRemoteOperationStatus.Success)
            { ropFails++; ropFailInfo = $"{res.Status}@{res.ErrorLocation} hr=0x{res.ExtendedError:X8}"; return; }
        }));
    }
    Report("prop batch (60 reads)");
}

// ------------------------------------------------------------ children: enumerate + 3 props

if (ops.Contains("children"))
{
    int comCount = 0, remCount = 0;
    var cacheReq = uia.CreateCacheRequest();
    cacheReq.AddProperty(30005); cacheReq.AddProperty(30003); cacheReq.AddProperty(30012);

    for (var r = 0; r < reps; r++)
    {
        comTimes.Add(T(() =>
        {
            var arr = root.FindAll(TreeScope.TreeScope_Children, uia.CreateTrueCondition());
            var names = new List<string>(arr.Length);
            for (var k = 0; k < arr.Length; k++)
            {
                var el = arr.GetElement(k).BuildUpdatedCache(cacheReq);
                names.Add($"{el.CachedName}|{el.CachedControlType}|{el.CachedClassName}");
            }
            comCount = arr.Length;
        }));

        ropTimes.Add(T(() =>
        {
            var (b, resOp) = BuildChildrenProgram();
            var p = new CoreAutomationRemoteOperation();
            p.ImportElement(OI(1), ToAutoElement(root));
            p.AddToResults(OI(resOp));
            var res = p.Execute(b);
            if (res.Status != AutomationRemoteOperationStatus.Success)
            { ropFails++; ropFailInfo = $"children: {res.Status}@{res.ErrorLocation} hr=0x{res.ExtendedError:X8}"; return; }
            var list = (IEnumerable)res.GetOperand(OI(resOp))!;
            remCount = list.Cast<object>().Count();
        }));
    }
    Report("children enum+3props", $"com={comCount} rem={remCount}");
}

// ------------------------------------------------------------ tree: full DFS subtree snapshot

if (ops.Contains("tree"))
{
    int comCount = 0, remCount = 0;
    var cacheReq = uia.CreateCacheRequest();
    cacheReq.AddProperty(30005); cacheReq.AddProperty(30003);
    cacheReq.AddProperty(30012); cacheReq.AddProperty(30001);

    for (var r = 0; r < reps; r++)
    {
        comTimes.Add(T(() =>
        {
            var arr = root.FindAllBuildCache(TreeScope.TreeScope_Descendants,
                uia.CreateTrueCondition(), cacheReq);
            var names = new List<string>(arr.Length);
            for (var k = 0; k < arr.Length; k++)
            {
                var el = arr.GetElement(k);
                names.Add($"{el.CachedName}|{el.CachedControlType}");
            }
            comCount = arr.Length;
        }));

        ropTimes.Add(T(() =>
        {
            var (b, resOp) = BuildTreeProgram();
            var p = new CoreAutomationRemoteOperation();
            p.ImportElement(OI(1), ToAutoElement(root));
            p.AddToResults(OI(resOp));
            var res = p.Execute(b);
            if (res.Status != AutomationRemoteOperationStatus.Success)
            { ropFails++; ropFailInfo = $"{res.Status}@{res.ErrorLocation} hr=0x{res.ExtendedError:X8}"; return; }
            var list = (IEnumerable)res.GetOperand(OI(resOp))!;
            remCount = list.Cast<object>().Count();
        }));
    }
    Report("subtree DFS+4props", $"com={comCount} rem={remCount}");
}

// ------------------------------------------------------------ ancestry

if (ops.Contains("ancestry"))
{
    IUIAutomationElement leaf = root;
    for (var d = 0; d < 6; d++)
    {
        var kids = leaf.FindAll(TreeScope.TreeScope_Children, uia.CreateTrueCondition());
        if (kids.Length == 0) break;
        leaf = kids.GetElement(0);
    }

    for (var r = 0; r < reps; r++)
    {
        comTimes.Add(T(() =>
        {
            var walker = uia.RawViewWalker;
            var chain = new List<string>();
            var el = leaf;
            for (var d = 0; d < 16 && el != null; d++)
            {
                var p = walker.GetParentElement(el);
                if (p == null) break;
                chain.Add($"{p.CurrentName}|{p.CurrentControlType}|{p.CurrentClassName}");
                el = p;
            }
        }));

        ropTimes.Add(T(() =>
        {
            var (b, resOp) = BuildAncestryProgram();
            var p = new CoreAutomationRemoteOperation();
            p.ImportElement(OI(1), ToAutoElement(leaf));
            p.AddToResults(OI(resOp));
            var res = p.Execute(b);
            if (res.Status != AutomationRemoteOperationStatus.Success)
            { ropFails++; ropFailInfo = $"ancestry: {res.Status}@{res.ErrorLocation} hr=0x{res.ExtendedError:X8}"; return; }
            var list = (IEnumerable)res.GetOperand(OI(resOp))!;
            _ = list.Cast<object>().Count();
        }));
    }
    Report("ancestry chain+3props");
}

// ------------------------------------------------------------ element mode: None vs Full

if (ops.Contains("mode"))
{
    int count = 0;
    var fullTimes = new List<double>();
    var noneTimes = new List<double>();
    for (var r = 0; r < reps; r++)
    {
        fullTimes.Add(T(() =>
        {
            var req = uia.CreateCacheRequest();
            req.AddProperty(30005); req.AddProperty(30003); req.AddProperty(30012);
            var arr = root.FindAllBuildCache(TreeScope.TreeScope_Descendants,
                uia.CreateTrueCondition(), req);
            count = arr.Length;
            // touch one cached prop per element — forces materialization check
            for (var k = 0; k < arr.Length; k++) _ = arr.GetElement(k).CachedName;
        }));
        noneTimes.Add(T(() =>
        {
            var req = uia.CreateCacheRequest();
            req.AddProperty(30005); req.AddProperty(30003); req.AddProperty(30012);
            req.AutomationElementMode = AutomationElementMode.AutomationElementMode_None;
            var arr = root.FindAllBuildCache(TreeScope.TreeScope_Descendants,
                uia.CreateTrueCondition(), req);
            count = arr.Length;
            for (var k = 0; k < arr.Length; k++) _ = arr.GetElement(k).CachedName;
        }));
    }
    var (mf, pf, xf) = Stats(fullTimes); var (mn, pn, xn) = Stats(noneTimes);
    Console.WriteLine($"-- element mode (descendants+3props) elements={count}");
    Console.WriteLine($"   FULL: median={mf,7:F1}ms p95={pf,7:F1}ms max={xf,7:F1}ms");
    Console.WriteLine($"   NONE: median={mn,7:F1}ms p95={pn,7:F1}ms max={xn,7:F1}ms");
}

// ------------------------------------------------------------ nav diagnostic

if (ops.Contains("nav"))
{
    var b = new Bc();
    var dir = b.Lit(); var cur = b.Lit(); var v = b.Lit(); var pN = b.Lit(); var ig = b.Lit();
    var cond = b.Lit();
    b.NewInt(dir, 3); b.NewInt(pN, 30005); b.NewBool(ig, false);
    b.Navigate(cur, 1, dir);            // first child
    b.IsNull(cond, cur);
    int pSkip = b.ForkIfTrue(cond, 0);  // skip prop read when null
    b.GetProp(v, cur, pN, ig);
    int iEnd = b.Count;
    b.PatchRel(pSkip, 0, Rel(pSkip, iEnd));
    b.Emit(0x05);
    var p = new CoreAutomationRemoteOperation();
    p.ImportElement(OI(1), ToAutoElement(root));
    p.AddToResults(OI(cond));
    p.AddToResults(OI(v));
    var res = p.Execute(b.Finish());
    Console.WriteLine($"nav diag: status={res.Status} errLoc={res.ErrorLocation}");
    if (res.Status == AutomationRemoteOperationStatus.Success)
    {
        var isNull = res.GetOperand(OI(cond));
        object? nm = res.HasOperand(OI(v)) ? res.GetOperand(OI(v)) : "<no operand>";
        Console.WriteLine($"  firstChild isNull={isNull} name={nm ?? "<null>"}");
        // compare: what does COM FindAll(Children) see?
        var arr = root.FindAll(TreeScope.TreeScope_Children, uia.CreateTrueCondition());
        Console.WriteLine($"  COM children={arr.Length} first=\"{SafeStr(() => arr.GetElement(0).CurrentName)}\"");
        // and raw walker first child
        var fc = uia.ControlViewWalker.GetFirstChildElement(root);
        Console.WriteLine($"  ControlViewWalker.FirstChild name=\"{SafeStr(() => fc?.CurrentName)}\"");
        var rfc = uia.RawViewWalker.GetFirstChildElement(root);
        Console.WriteLine($"  RawViewWalker.FirstChild name=\"{SafeStr(() => rfc?.CurrentName)}\"");

        // navigate UP from an imported child — does Parent work?
        if (arr.Length > 0)
        {
            var kid = arr.GetElement(0);
            var b2 = new Bc();
            var dP = b2.Lit(); var par = b2.Lit(); var pv = b2.Lit(); var pN2 = b2.Lit(); var ig2 = b2.Lit(); var c2 = b2.Lit();
            b2.NewInt(dP, 0); b2.NewInt(pN2, 30005); b2.NewBool(ig2, false);
            b2.Navigate(par, 1, dP);           // parent of imported child
            b2.IsNull(c2, par);
            b2.GetProp(pv, par, pN2, ig2);
            b2.Emit(0x05);
            var p2 = new CoreAutomationRemoteOperation();
            p2.ImportElement(OI(1), ToAutoElement(kid));
            p2.AddToResults(OI(c2));
            p2.AddToResults(OI(pv));
            var res2 = p2.Execute(b2.Finish());
            Console.WriteLine($"  parent nav: status={res2.Status} " +
                (res2.Status == AutomationRemoteOperationStatus.Success
                    ? $"isNull={res2.GetOperand(OI(c2))} name={res2.GetOperand(OI(pv)) ?? "<null>"}"
                    : $"errLoc={res2.ErrorLocation}"));
        }
    }
}

Console.WriteLine("done.");
return 0;

// ============================================================ programs

(byte[] bc, uint resultsOp) BuildChildrenProgram()
{
    var b = new Bc();
    var dir = b.Lit(); var dirN = b.Lit(); var pN = b.Lit(); var pC = b.Lit(); var pCl = b.Lit();
    var ig = b.Lit(); var arr = b.Lit(); var cur = b.Lit(); var cond = b.Lit();
    var m = b.Lit(); var v = b.Lit(); var kn = b.Lit(); var kc = b.Lit(); var kcl = b.Lit();
    b.NewInt(dir, 3); b.NewInt(dirN, 1);
    b.NewInt(pN, 30005); b.NewInt(pC, 30003); b.NewInt(pCl, 30012);
    b.NewBool(ig, false);
    b.NewString(kn, "name"); b.NewString(kc, "ct"); b.NewString(kcl, "cls");
    b.NewArray(arr);
    b.Navigate(cur, 1, dir);
    int iLoop = b.Count;
    int pLoop = b.NewLoop(0, 0);
    int iBody = b.Count;
    b.IsNull(cond, cur);
    int pFork = b.ForkIfTrue(cond, 0);
    b.NewStringMap(m);
    b.GetProp(v, cur, pN, ig); b.MapInsert(m, kn, v);
    b.GetProp(v, cur, pC, ig); b.MapInsert(m, kc, v);
    b.GetProp(v, cur, pCl, ig); b.MapInsert(m, kcl, v);
    b.ArrAppend(arr, m);
    b.Navigate(cur, cur, dirN);
    b.ContinueLoop();
    int iBrk = b.Count;
    b.BreakLoop();
    b.EndLoop();
    int iDone = b.Count;
    b.PatchRel(pLoop, 0, Rel(iLoop, iDone));
    b.PatchRel(pLoop, 1, Rel(iLoop, iBody));
    b.PatchRel(pFork, 0, Rel(pFork, iBrk));
    b.Emit(0x05);
    return (b.Finish(), arr);
}

(byte[] bc, uint resultsOp) BuildTreeProgram()
{
    var b = new Bc();
    var dF = b.Lit(); var dN = b.Lit();
    var pN = b.Lit(); var pC = b.Lit(); var pCl = b.Lit(); var pR = b.Lit();
    var ig = b.Lit(); var st = b.Lit(); var res = b.Lit();
    var cur = b.Lit(); var sib = b.Lit(); var kid = b.Lit();
    var n = b.Lit(); var idx = b.Lit(); var eq = b.Lit(); var cond = b.Lit();
    var m = b.Lit(); var v = b.Lit();
    var kn = b.Lit(); var kc = b.Lit(); var kcl = b.Lit(); var kr = b.Lit();
    var zero = b.Lit(); var one = b.Lit();

    b.NewInt(dF, 3); b.NewInt(dN, 1);
    b.NewInt(pN, 30005); b.NewInt(pC, 30003); b.NewInt(pCl, 30012); b.NewInt(pR, 30001);
    b.NewBool(ig, false);
    b.NewUint(zero, 0); b.NewUint(one, 1);
    b.NewString(kn, "name"); b.NewString(kc, "ct"); b.NewString(kcl, "cls"); b.NewString(kr, "rect");
    b.NewArray(st); b.NewArray(res);

    b.Navigate(cur, 1, dF);
    b.IsNull(cond, cur);
    int pSeedFork = b.ForkIfTrue(cond, 0);
    b.ArrAppend(st, cur);

    int iLoop = b.Count;
    int pLoop = b.NewLoop(0, 0);
    int iBody = b.Count;
    b.ArrSize(n, st);
    b.Compare(eq, n, zero, 0);
    int pEmpty = b.ForkIfTrue(eq, 0);
    b.Sub(idx, n, one);
    b.ArrGetAt(cur, st, idx);
    b.ArrRemoveAt(st, idx);
    b.NewStringMap(m);
    b.GetProp(v, cur, pN, ig); b.MapInsert(m, kn, v);
    b.GetProp(v, cur, pC, ig); b.MapInsert(m, kc, v);
    b.GetProp(v, cur, pCl, ig); b.MapInsert(m, kcl, v);
    b.GetProp(v, cur, pR, ig); b.MapInsert(m, kr, v);
    b.ArrAppend(res, m);
    b.Navigate(sib, cur, dN);
    b.IsNull(cond, sib);
    int pNoSib = b.ForkIfTrue(cond, 0);
    b.ArrAppend(st, sib);
    int iNoSib = b.Count;
    b.Navigate(kid, cur, dF);
    b.IsNull(cond, kid);
    int pNoKid = b.ForkIfTrue(cond, 0);
    b.ArrAppend(st, kid);
    int iNoKid = b.Count;
    b.ContinueLoop();
    int iBrk = b.Count;
    b.BreakLoop();
    b.EndLoop();
    int iDone = b.Count;

    b.PatchRel(pSeedFork, 0, Rel(pSeedFork, iDone));
    b.PatchRel(pLoop, 0, Rel(iLoop, iDone));
    b.PatchRel(pLoop, 1, Rel(iLoop, iBody));
    b.PatchRel(pEmpty, 0, Rel(pEmpty, iBrk));
    b.PatchRel(pNoSib, 0, Rel(pNoSib, iNoSib));
    b.PatchRel(pNoKid, 0, Rel(pNoKid, iNoKid));
    b.Emit(0x05);
    return (b.Finish(), res);
}

(byte[] bc, uint resultsOp) BuildAncestryProgram()
{
    var b = new Bc();
    var dP = b.Lit();
    var pN = b.Lit(); var pC = b.Lit(); var pCl = b.Lit();
    var ig = b.Lit(); var res = b.Lit();
    var cur = b.Lit(); var cond = b.Lit(); var m = b.Lit(); var v = b.Lit();
    var kn = b.Lit(); var kc = b.Lit(); var kcl = b.Lit();
    var dMax = b.Lit(); var dCnt = b.Lit(); var one = b.Lit(); var eq = b.Lit();

    b.NewInt(dP, 0);
    b.NewInt(pN, 30005); b.NewInt(pC, 30003); b.NewInt(pCl, 30012);
    b.NewBool(ig, false);
    b.NewInt(dMax, 16); b.NewInt(dCnt, 0); b.NewInt(one, 1);
    b.NewString(kn, "name"); b.NewString(kc, "ct"); b.NewString(kcl, "cls");
    b.NewArray(res);
    b.Set(cur, 1);

    int iLoop = b.Count;
    int pLoop = b.NewLoop(0, 0);
    int iBody = b.Count;
    b.Navigate(cur, cur, dP);
    b.IsNull(cond, cur);
    int pNull = b.ForkIfTrue(cond, 0);
    b.Compare(eq, dCnt, dMax, 0);
    int pMax = b.ForkIfTrue(eq, 0);
    b.NewStringMap(m);
    b.GetProp(v, cur, pN, ig); b.MapInsert(m, kn, v);
    b.GetProp(v, cur, pC, ig); b.MapInsert(m, kc, v);
    b.GetProp(v, cur, pCl, ig); b.MapInsert(m, kcl, v);
    b.ArrAppend(res, m);
    b.Add(dCnt, one);
    b.ContinueLoop();
    int iBrk = b.Count;
    b.BreakLoop();
    b.EndLoop();
    int iDone = b.Count;

    b.PatchRel(pLoop, 0, Rel(iLoop, iDone));
    b.PatchRel(pLoop, 1, Rel(iLoop, iBody));
    b.PatchRel(pNull, 0, Rel(pNull, iBrk));
    b.PatchRel(pMax, 0, Rel(pMax, iBrk));
    b.Emit(0x05);
    return (b.Finish(), res);
}

// ============================================================ helpers

static Windows.UI.UIAutomation.AutomationElement ToAutoElement(IUIAutomationElement el)
{
    // The system UIA element is a connection-bound object: it already
    // implements IInspectable — QI it and wrap with the CsWinRT projection.
    var unk = Marshal.GetIUnknownForObject(el);
    try
    {
        var iid = new Guid("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90"); // IInspectable
        Marshal.QueryInterface(unk, ref iid, out var ins);
        try { return Windows.UI.UIAutomation.AutomationElement.FromAbi(ins); }
        finally { Marshal.Release(ins); }
    }
    finally { Marshal.Release(unk); }
}

static IntPtr FindWindowBy(int? pid, string? title, string? cls)
{
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, _) =>
    {
        if (cls == null && !IsWindowVisible(h)) return true;
        GetWindowThreadProcessId(h, out var p);
        if (pid != null && p != pid) return true;
        if (cls != null)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() != cls) return true;
        }
        if (title != null)
        {
            var t = WindowText(h);
            if (!t.Contains(title, StringComparison.OrdinalIgnoreCase)) return true;
        }
        found = h;
        return false;
    }, IntPtr.Zero);
    return found;
}

static string WindowText(IntPtr h)
{
    var sb = new StringBuilder(512);
    GetWindowTextW(h, sb, sb.Capacity);
    return sb.ToString();
}

static string SafeStr(Func<string?> f) { try { return f() ?? ""; } catch { return "?"; } }

[DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

// ------------------------------------------------------------ bytecode builder
// Instruction = int32 opcode + packed params (operand ids u32, inline i32).
// NewLoop/ForkIfTrue return the emitting instruction's index so relative
// offsets can be patched after layout. Patch convention is empirical.

delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

sealed class Bc
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _w;
    private uint _nextOperand = 2; // operand 1 = imported element
    private int _instrCount;
    private readonly Dictionary<int, long> _paramPos = new(); // instrIndex → byte pos of first i32 param

    public Bc() => _w = new BinaryWriter(_ms);

    public int Count => _instrCount;
    public uint Lit() => _nextOperand++;

    private void Op(uint opcode) { _w.Write(opcode); _instrCount++; }
    private void O(uint operandId) => _w.Write(operandId);

    public void NewInt(uint r, int v) { Op(0x1D); O(r); _w.Write(v); }
    public void NewUint(uint r, uint v) { Op(0x1E); O(r); _w.Write(v); }
    public void NewBool(uint r, bool v) { Op(0x1F); O(r); _w.Write(v ? (byte)1 : (byte)0); }
    public void NewString(uint r, string s)
    {
        Op(0x22); O(r); _w.Write((uint)s.Length);
        foreach (var c in s) _w.Write((ushort)c);
    }
    public void NewArray(uint r) { Op(0x25); O(r); }
    public void NewStringMap(uint r) { Op(0x26); O(r); }
    public void Set(uint t, uint v) { Op(0x01); O(t); O(v); }
    public void Compare(uint r, uint l, uint rr, int cmp) { Op(0x1C); O(r); O(l); O(rr); _w.Write(cmp); }
    public void Add(uint t, uint v) { Op(0x0E); O(t); O(v); } // in-place: target += value
    public void Sub(uint r, uint l, uint rr) { Op(0x13); O(r); O(l); O(rr); } // BinarySubtract
    public void GetProp(uint r, uint t, uint prop, uint ign) { Op(0x38); O(r); O(t); O(prop); O(ign); }
    public void Navigate(uint r, uint t, uint dir) { Op(0x39); O(r); O(t); O(dir); }
    public void IsNull(uint r, uint t) { Op(0x3A); O(r); O(t); }
    public void ArrAppend(uint arr, uint v) { Op(0x2A); O(arr); O(v); }
    public void ArrGetAt(uint r, uint arr, uint i) { Op(0x2D); O(r); O(arr); O(i); }
    public void ArrRemoveAt(uint arr, uint i) { Op(0x2C); O(arr); O(i); }
    public void ArrSize(uint r, uint arr) { Op(0x2E); O(r); O(arr); }
    public void MapInsert(uint m, uint k, uint v) { Op(0x2F); O(m); O(k); O(v); }
    public void Emit(uint opcode) => Op(opcode);
    public void ContinueLoop() => Op(0x09);
    public void BreakLoop() => Op(0x08);
    public void EndLoop() => Op(0x07);

    /// <summary>NewLoopBlock(breakRel, contRel); returns instruction index for patching.</summary>
    public int NewLoop(int breakRel, int contRel)
    {
        var idx = _instrCount;
        Op(0x06);
        _paramPos[idx] = _ms.Position;
        _w.Write(breakRel); _w.Write(contRel);
        return idx;
    }

    /// <summary>ForkIfTrue(cond, rel); returns instruction index for patching.</summary>
    public int ForkIfTrue(uint cond, int rel)
    {
        var idx = _instrCount;
        Op(0x02); O(cond);
        _paramPos[idx] = _ms.Position;
        _w.Write(rel);
        return idx;
    }

    /// <summary>Patches i32 param <paramref name="slot"/> of the instruction
    /// emitted at <paramref name="instrIndex"/> (NewLoop: 0=break,1=cont;
    /// ForkIfTrue: 0=rel).</summary>
    public void PatchRel(int instrIndex, int slot, int rel)
    {
        var pos = _ms.Position;
        _ms.Position = _paramPos[instrIndex] + slot * 4;
        _w.Write(rel);
        _ms.Position = pos;
    }

    public byte[] Finish()
    {
        _w.Flush();
        var hdr = BitConverter.GetBytes(0); // bytecode version = 0
        return hdr.Concat(_ms.ToArray()).ToArray();
    }
}
