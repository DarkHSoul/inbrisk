using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inbrisk.Core;
using Microsoft.Win32;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Specialist adapter for Blender via a localhost JSON-lines socket bridge.
/// Blender exposes no UIA tree (custom OpenGL viewport), so semantic control
/// goes through its embedded Python interpreter (<c>bpy</c>): a small bridge
/// script runs inside Blender, listens on 127.0.0.1, and executes requests on
/// Blender's main thread via <c>bpy.app.timers</c>. The C# side sends one JSON
/// line per request and reads one JSON line back — the same request/response
/// shape used by the 3dsmax-mcp bridge.
///
/// Setup is one-time: run <c>computer_adapter{adapter:"blender",
/// action:"auto_install"}</c> to drop the bridge into Blender's
/// <c>scripts\startup</c> folder so it loads automatically on every launch.
/// Alternatively <c>action:"bootstrap"</c> materializes the script to
/// %LOCALAPPDATA% for a manual run (Scripting workspace → Run Script, or
/// <c>blender --python blender_bridge.py</c>). When the bridge is not
/// reachable every action fails gracefully with install guidance instead of
/// throwing.
/// </summary>
public sealed class BlenderAdapter : IApplicationAdapter
{
    public string AdapterId => "blender";
    public string DisplayName => "Blender bpy Socket Bridge Adapter";

    public IReadOnlyList<string> SupportedActions { get; } = new[]
    {
        "execute", "eval", "status", "get_scene", "bootstrap", "auto_install"
    };

    /// <summary>Default localhost port the embedded bridge listens on.
    /// Overridable per call via args["port"] or the INBRISK_BLENDER_PORT
    /// environment variable (must match the bridge's port).</summary>
    public const int DefaultPort = 9877;

    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 300_000;

    /// <summary>Where <see cref="BootstrapScript"/> is materialized for the
    /// user to load inside Blender.</summary>
    public static string BootstrapPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Inbrisk", "adapters", "blender_bridge.py");

    public bool IsApplicable(string? processName, long? hwnd)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return clean.Contains("blender", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanHandle(string action, TargetRef? target = null, IReadOnlyDictionary<string, object?>? args = null)
    {
        var act = (action ?? "").ToLowerInvariant();
        return act is "execute" or "exec" or "eval" or "evaluate"
            or "status" or "ping" or "get_scene" or "scene" or "bootstrap"
            or "auto_install" or "install";
    }

    public async Task<AdapterResult> ExecuteAsync(
        string action,
        TargetRef? target = null,
        IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var act = (action ?? "").ToLowerInvariant();
        var port = ResolvePort(args);
        var timeoutMs = ResolveTimeoutMs(args);

        try
        {
            switch (act)
            {
                case "bootstrap":
                    return await Task.Run(() => Bootstrap(), ct).ConfigureAwait(false);

                case "auto_install":
                case "install":
                    return await Task.Run(() => AutoInstall(), ct).ConfigureAwait(false);

                case "status":
                case "ping":
                {
                    var res = await SendRequestAsync(port,
                        new JsonObject { ["type"] = "ping" }, timeoutMs, ct).ConfigureAwait(false);
                    return BridgeResult("Blender.Status", res,
                        okDetail: r => $"Connected to Blender {r?["result"]?["blender"] ?? "?"} bridge on 127.0.0.1:{port}");
                }

                case "execute":
                case "exec":
                {
                    var code = GetArgString(args, "code", GetArgString(args, "script", ""));
                    if (string.IsNullOrWhiteSpace(code))
                        return new AdapterResult(false, "Blender.Execute",
                            "code argument is required (bpy Python to execute inside Blender)",
                            Error: ErrorCode.Unsupported);

                    var res = await SendRequestAsync(port,
                        new JsonObject
                        {
                            ["type"] = "execute",
                            ["code"] = code,
                            ["timeout"] = timeoutMs / 1000.0,
                        }, timeoutMs, ct).ConfigureAwait(false);
                    return BridgeResult("Blender.Execute", res,
                        okDetail: r => $"bpy code executed (stdout: {Trunc(r?["stdout"]?.ToString(), 120)})");
                }

                case "eval":
                case "evaluate":
                {
                    var expr = GetArgString(args, "code",
                        GetArgString(args, "expression", GetArgString(args, "script", "")));
                    if (string.IsNullOrWhiteSpace(expr))
                        return new AdapterResult(false, "Blender.Eval",
                            "code/expression argument is required (Python expression evaluated inside Blender)",
                            Error: ErrorCode.Unsupported);

                    var res = await SendRequestAsync(port,
                        new JsonObject
                        {
                            ["type"] = "eval",
                            ["code"] = expr,
                            ["timeout"] = timeoutMs / 1000.0,
                        }, timeoutMs, ct).ConfigureAwait(false);
                    return BridgeResult("Blender.Eval", res,
                        okDetail: r => $"evaluated: {Trunc(r?["result"]?.ToJsonString(), 200)}");
                }

                case "get_scene":
                case "scene":
                {
                    var res = await SendRequestAsync(port,
                        new JsonObject
                        {
                            ["type"] = "eval",
                            ["code"] = SceneSummaryExpr,
                            ["timeout"] = timeoutMs / 1000.0,
                        }, timeoutMs, ct).ConfigureAwait(false);
                    return BridgeResult("Blender.GetScene", res,
                        okDetail: r =>
                        {
                            var s = r?["result"];
                            return $"Scene '{s?["scene"]}': {s?["objectCount"]} objects, mode {s?["mode"]}, frame {s?["frame"]}";
                        });
                }

                default:
                    return new AdapterResult(false, "Blender.UnknownAction",
                        $"unrecognized Blender adapter action '{action}'",
                        Error: ErrorCode.Unsupported);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AdapterResult(false, "Blender.Timeout",
                $"Blender bridge on 127.0.0.1:{port} did not answer within {timeoutMs}ms — " +
                "Blender may be busy in a modal operator or the bridge may be wedged",
                Error: ErrorCode.Timeout);
        }
        catch (SocketException ex)
        {
            return Unreachable(port, ex.Message);
        }
        catch (Exception ex)
        {
            return new AdapterResult(false, "Blender.ExecutionError", ex.Message,
                Error: ErrorCode.Internal);
        }
    }

    /// <summary>Materializes the embedded bridge script to
    /// <see cref="BootstrapPath"/> so the user can load it once inside
    /// Blender. The script text is also returned in the payload for
    /// clipboard/injection flows.</summary>
    private static AdapterResult Bootstrap()
    {
        var path = BootstrapPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BootstrapScript);
        return new AdapterResult(
            Success: true,
            Method: "Blender.Bootstrap",
            Detail: $"Bridge script written to {path}. Run it once inside Blender " +
                    "(Scripting workspace → open → Run Script, or start Blender with " +
                    $"`blender --python \"{path}\"`). It listens on 127.0.0.1:{DefaultPort} " +
                    "until Blender exits; re-run after each Blender restart.",
            Data: new Dictionary<string, object?>
            {
                ["path"] = path,
                ["port"] = DefaultPort,
                ["portEnvVar"] = "INBRISK_BLENDER_PORT",
                ["script"] = BootstrapScript,
            });
    }

    /// <summary>File name written into Blender's <c>scripts\startup</c>
    /// folder. Blender executes every *.py there at launch, so the bridge
    /// starts with Blender — no manual Scripting-workspace step.</summary>
    private const string StartupFileName = "inbrisk_bridge.py";

    /// <summary>
    /// Installs the bridge into Blender's startup-scripts folder so it is
    /// auto-loaded on every Blender launch. Search order:
    /// <list type="number">
    /// <item><c>%APPDATA%\Blender Foundation\Blender\&lt;ver&gt;\scripts\startup</c>
    /// — highest versioned subdir wins (this is Blender's canonical per-user
    /// startup location; dirs are created if missing).</item>
    /// <item><c>C:\Program Files\Blender Foundation\Blender *&lt;ver&gt;\&lt;ver&gt;\scripts\startup</c>
    /// — versioned subdirs of each system-wide install dir.</item>
    /// <item><c>scripts\startup</c> beside a blender.exe located through the
    /// registry App Paths key (HKLM/HKCU).</item>
    /// </list>
    /// Idempotent: identical content on disk is reported as
    /// "already-installed" and left untouched.
    /// </summary>
    private static AdapterResult AutoInstall()
    {
        var candidates = FindStartupDirs();
        if (candidates.Count == 0)
        {
            return new AdapterResult(false, "Blender.AutoInstall",
                "No Blender installation found. Searched " +
                $"'{RoamingBlenderRoot}\\<version>', " +
                $"'{ProgramFilesBlenderRoot}\\Blender *\\<version>', and " +
                "the scripts\\startup dir next to blender.exe from the " +
                "HKLM/HKCU App Paths registry keys. Install Blender first, or " +
                "use action \"bootstrap\" and run the script manually inside Blender.",
                Error: ErrorCode.NotFound);
        }

        var chosen = candidates[0];
        Directory.CreateDirectory(chosen.StartupDir);
        var target = Path.Combine(chosen.StartupDir, StartupFileName);

        var status = "installed";
        if (File.Exists(target) &&
            string.Equals(File.ReadAllText(target), BootstrapScript, StringComparison.Ordinal))
        {
            status = "already-installed";
        }
        else
        {
            File.WriteAllText(target, BootstrapScript);
        }

        var versionLabel = chosen.VersionText ?? "unknown";
        return new AdapterResult(true, "Blender.AutoInstall",
            status == "already-installed"
                ? $"Bridge already installed at {target} (identical content). " +
                  "Restart Blender if it is running to (re)load the bridge; " +
                  $"it listens on 127.0.0.1:{DefaultPort}."
                : $"Bridge installed to {target} (Blender {versionLabel}, via " +
                  $"{chosen.Source}). Restart Blender — startup scripts run at " +
                  $"launch, then the bridge listens on 127.0.0.1:{DefaultPort}.",
            Data: new Dictionary<string, object?>
            {
                ["installedTo"] = target,
                ["blenderVersion"] = versionLabel,
                ["status"] = status,
                ["howToActivate"] = "restart Blender",
                ["port"] = DefaultPort,
                ["source"] = chosen.Source,
            });
    }

    /// <summary>One candidate startup folder: the parsed Blender version
    /// (null when the dir layout gave no version), its display string, the
    /// scripts\startup path, and which search tier found it.</summary>
    private sealed record StartupCandidate(
        Version? Version, string? VersionText, string StartupDir, string Source);

    private static string RoamingBlenderRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Blender Foundation", "Blender");

    private static string ProgramFilesBlenderRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Blender Foundation");

    /// <summary>Ordered candidate startup dirs, highest Blender version
    /// first. Tier 1 (roaming config) is authoritative — Blender always
    /// reads it — so tiers 2–3 are only consulted when no versioned
    /// roaming dir exists yet (e.g. Blender installed but never run).</summary>
    private static List<StartupCandidate> FindStartupDirs()
    {
        var found = VersionedStartupDirs(RoamingBlenderRoot, "roaming-config").ToList();
        if (found.Count == 0)
        {
            if (Directory.Exists(ProgramFilesBlenderRoot))
            {
                foreach (var installDir in SafeEnumerateDirs(ProgramFilesBlenderRoot, "Blender*"))
                    found.AddRange(VersionedStartupDirs(installDir, "program-files"));
            }

            var exe = FindBlenderExe();
            if (exe != null)
            {
                var exeDir = Path.GetDirectoryName(exe)!;
                found.AddRange(VersionedStartupDirs(exeDir, "exe-adjacent"));
                var direct = Path.Combine(exeDir, "scripts", "startup");
                if (Directory.Exists(direct))
                    found.Add(new StartupCandidate(null, null, direct, "exe-adjacent"));
            }

            found = found
                .GroupBy(c => c.StartupDir, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        return found
            .OrderByDescending(c => c.Version ?? new Version(0, 0))
            .ToList();
    }

    /// <summary>Versioned subdirs of <paramref name="root"/> ("3.6", "4.2",
    /// …) each mapped to its <c>scripts\startup</c> child. The child need
    /// not exist — it is created on install.</summary>
    private static IEnumerable<StartupCandidate> VersionedStartupDirs(string root, string source)
    {
        foreach (var sub in SafeEnumerateDirs(root))
        {
            var name = Path.GetFileName(sub);
            if (Version.TryParse(name, out var v))
                yield return new StartupCandidate(v, name,
                    Path.Combine(sub, "scripts", "startup"), source);
        }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string root, string pattern = "*")
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, pattern).ToList()
                : new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>Locates blender.exe through the App Paths registry key
    /// (what the Blender installer registers) in both HKLM and HKCU and
    /// both 64/32-bit views. Null when no registration exists; registry
    /// access is best-effort (non-Windows simply returns null).</summary>
    private static string? FindBlenderExe()
    {
        if (!OperatingSystem.IsWindows()) return null;
        const string appPathsKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\blender.exe";
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(appPathsKey);
                var val = key?.GetValue(null) as string; // (Default) value
                if (!string.IsNullOrWhiteSpace(val) && File.Exists(val))
                    return val;
            }
            catch (Exception)
            {
                // registry unavailable / access denied — keep searching
            }
        }
        return null;
    }

    /// <summary>Wraps a raw bridge response in an AdapterResult; the bridge's
    /// ok:false carries the Python traceback through as the detail.</summary>
    private static AdapterResult BridgeResult(string method, JsonNode? res,
        Func<JsonNode?, string> okDetail)
    {
        if (res == null)
            return new AdapterResult(false, method,
                "empty response from Blender bridge", Error: ErrorCode.Internal);
        var ok = res["ok"]?.GetValue<bool>() ?? false;
        if (!ok)
            return new AdapterResult(false, method,
                res["error"]?.ToString() ?? "Blender bridge reported failure",
                Data: new Dictionary<string, object?> { ["raw"] = res.ToJsonString() },
                Error: ErrorCode.Internal);
        return new AdapterResult(true, method, okDetail(res),
            Data: new Dictionary<string, object?>
            {
                ["result"] = res["result"]?.DeepClone(),
                ["stdout"] = res["stdout"]?.ToString(),
                ["id"] = res["id"]?.ToString(),
            });
    }

    private AdapterResult Unreachable(int port, string why)
    {
        const string hint =
            "Run computer_adapter{adapter:\"blender\", action:\"auto_install\"} to install " +
            "the bridge into Blender's scripts\\startup folder, then restart Blender " +
            "(alternative: action:\"bootstrap\" writes the script for a manual run in " +
            "the Scripting workspace).";
        return new AdapterResult(
            Success: false,
            Method: "Blender.Connect",
            Detail: IsBlenderProcessRunning()
                ? $"Blender bpy bridge is not reachable on 127.0.0.1:{port} ({why}), but a " +
                  "Blender process IS running — the bridge was never loaded in it or " +
                  $"Blender was started before install. {hint}"
                : $"Blender bpy bridge is not reachable on 127.0.0.1:{port} ({why}). {hint}",
            Error: ErrorCode.NotFound);
    }

    /// <summary>True when a blender.exe process exists — used to sharpen the
    /// "bridge unreachable" guidance (running-but-no-bridge is almost always
    /// "bridge never installed into startup scripts").</summary>
    private static bool IsBlenderProcessRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("blender");
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>One request = one short-lived connection: write a single JSON
    /// line, read a single JSON line back. The bridge serializes execution
    /// through bpy.app.timers on Blender's main thread, so no client-side
    /// connection pooling is needed.</summary>
    private static async Task<JsonNode?> SendRequestAsync(
        int port, JsonObject payload, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString() + "\n");
        await stream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        return line is { Length: > 0 } ? JsonNode.Parse(line) : null;
    }

    private static int ResolvePort(IReadOnlyDictionary<string, object?>? args)
    {
        if (args != null && args.TryGetValue("port", out var p) &&
            int.TryParse(p?.ToString(), out var parsed) && parsed is > 0 and < 65536)
            return parsed;
        var env = Environment.GetEnvironmentVariable("INBRISK_BLENDER_PORT");
        return int.TryParse(env, out var envPort) && envPort is > 0 and < 65536
            ? envPort : DefaultPort;
    }

    private static int ResolveTimeoutMs(IReadOnlyDictionary<string, object?>? args)
    {
        if (args != null && args.TryGetValue("timeoutMs", out var t) &&
            int.TryParse(t?.ToString(), out var ms))
            return Math.Clamp(ms, 500, MaxTimeoutMs);
        return DefaultTimeoutMs;
    }

    private static string GetArgString(IReadOnlyDictionary<string, object?>? args, string key, string fallback)
    {
        if (args != null && args.TryGetValue(key, out var val) && val != null)
            return val.ToString() ?? fallback;
        return fallback;
    }

    private static string? Trunc(string? s, int n) =>
        s != null && s.Length > n ? s[..n] + "…" : s;

    /// <summary>bpy expression producing a compact JSON-safe scene summary
    /// for the get_scene action.</summary>
    private const string SceneSummaryExpr =
        "{\"blender\": bpy.app.version_string, \"file\": bpy.data.filepath, " +
        "\"scene\": bpy.context.scene.name if bpy.context.scene else None, " +
        "\"mode\": getattr(bpy.context, \"mode\", None), " +
        "\"frame\": bpy.context.scene.frame_current if bpy.context.scene else None, " +
        "\"objectCount\": len(bpy.data.objects), " +
        "\"objects\": [o.name for o in list(bpy.data.objects)[:50]], " +
        "\"active\": bpy.context.active_object.name if bpy.context.active_object else None}";

    /// <summary>
    /// The Blender-side bridge, materialized verbatim by the "bootstrap"
    /// action. Design: a ThreadingTCPServer accepts one JSON line per
    /// connection and enqueues a job; a bpy.app.timers pump drains the queue
    /// on Blender's main thread (bpy is not thread-safe — socket threads must
    /// never call bpy directly) and posts the result back to the waiting
    /// handler. Re-runnable: re-executing the script cleanly replaces a
    /// previous instance.
    /// </summary>
    internal const string BootstrapScript = """
        # Inbrisk Blender bridge — auto-loaded when placed in Blender's
        # scripts/startup folder (see adapter action "auto_install"), or run
        # manually once inside Blender:
        #   Scripting workspace -> open this file -> "Run Script", or
        #   start Blender with:  blender --python blender_bridge.py
        # Listens on 127.0.0.1 (INBRISK_BLENDER_PORT env overrides, default 9877).
        # Protocol: one JSON request line -> one JSON response line.
        #   {"type":"execute","code":"<bpy python>","timeout":60}
        #   {"type":"eval","code":"<python expression>"}
        #   {"type":"ping"}   {"type":"stop"}
        import bpy, contextlib, io, json, os, queue, socketserver, threading, time, traceback, uuid

        _PORT = int(os.environ.get("INBRISK_BLENDER_PORT", "9877"))
        _jobs = queue.Queue()
        _results = {}
        _results_lock = threading.Lock()
        _server = None

        def _run(code, mode):
            buf = io.StringIO()
            ns = {"bpy": bpy}
            try:
                with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
                    if mode == "eval":
                        out = eval(code, ns)
                    else:
                        exec(code, ns)
                        # convention: exec'd code may assign `result` or `_`
                        out = ns.get("result", ns.get("_"))
                try:
                    json.dumps(out)
                    result = out
                except (TypeError, ValueError):
                    result = repr(out)
                return {"ok": True, "result": result, "stdout": buf.getvalue()}
            except Exception:
                return {"ok": False, "error": traceback.format_exc(limit=10),
                        "stdout": buf.getvalue()}

        def _ping():
            sc = bpy.context.scene
            return {"ok": True, "result": {
                "blender": bpy.app.version_string,
                "file": bpy.data.filepath,
                "scene": sc.name if sc else None,
                "mode": getattr(bpy.context, "mode", None),
                "objects": len(bpy.data.objects),
            }}

        # Runs on Blender's MAIN thread via bpy.app.timers — the only thread
        # allowed to touch bpy. Drains pending jobs; returns the re-arm interval.
        def _pump():
            while True:
                try:
                    jid, kind, code, tout = _jobs.get_nowait()
                except queue.Empty:
                    break
                if kind == "ping":
                    res = _ping()
                elif kind == "stop":
                    res = {"ok": True, "result": "stopping"}
                    threading.Thread(target=_shutdown, daemon=True).start()
                else:
                    res = _run(code, "eval" if kind == "eval" else "exec")
                with _results_lock:
                    _results[jid] = res
            return 0.05

        def _shutdown():
            try:
                if _server:
                    _server.shutdown()
            except Exception:
                pass

        class _Handler(socketserver.StreamRequestHandler):
            def handle(self):
                try:
                    line = self.rfile.readline(8 * 1024 * 1024)
                    req = json.loads(line.decode("utf-8"))
                except Exception as e:
                    self.wfile.write((json.dumps(
                        {"ok": False, "error": "bad request: %s" % e}) + "\n").encode())
                    return
                jid = uuid.uuid4().hex
                tout = min(float(req.get("timeout", 60)), 300.0)
                _jobs.put((jid, req.get("type", "execute"), req.get("code", ""), tout))
                deadline = time.time() + tout
                while time.time() < deadline:
                    with _results_lock:
                        res = _results.pop(jid, None)
                    if res is not None:
                        res["id"] = jid
                        self.wfile.write((json.dumps(res) + "\n").encode())
                        return
                    time.sleep(0.01)
                self.wfile.write((json.dumps(
                    {"ok": False, "id": jid,
                     "error": "timeout waiting for Blender main thread"}) + "\n").encode())

        class _Server(socketserver.ThreadingTCPServer):
            allow_reuse_address = True
            daemon_threads = True

        # Port-in-use retry: when scripts/startup auto-runs this file while a
        # previous Blender (or a stale socket) still owns the port, the bind
        # raises OSError. Instead of dying, a persistent timer retries the
        # bind so the bridge comes up as soon as the port frees.
        _RETRY = {"attempts": 0}
        _MAX_RETRY_ATTEMPTS = 240  # ~2 minutes at 0.5 s intervals

        def _bind(port):
            return _Server(("127.0.0.1", port), _Handler)

        def _serve(srv):
            global _server
            _server = srv
            threading.Thread(target=srv.serve_forever, daemon=True).start()
            if not bpy.app.timers.is_registered(_pump):
                bpy.app.timers.register(_pump, persistent=True)
            print("inbrisk bridge listening on 127.0.0.1:%d" % srv.server_address[1])

        def _retry_start():
            _RETRY["attempts"] += 1
            if _RETRY["attempts"] > _MAX_RETRY_ATTEMPTS:
                print("inbrisk bridge: giving up, port %d still busy" % _PORT)
                return None  # returning None unregisters this timer
            try:
                srv = _bind(_PORT)
            except OSError:
                return 0.5  # retry in 0.5 s
            _serve(srv)
            return None

        def stop():
            global _server
            for fn in (_pump, _retry_start):
                if bpy.app.timers.is_registered(fn):
                    bpy.app.timers.unregister(fn)
            if _server:
                _server.shutdown()
                _server.server_close()
                _server = None

        def start(port=None):
            global _server
            stop()  # re-running this script replaces a previous instance
            _RETRY["attempts"] = 0
            try:
                _serve(_bind(port or _PORT))
            except OSError as e:
                print("inbrisk bridge: 127.0.0.1:%d busy (%s); retrying via timers"
                      % (port or _PORT, e))
                if not bpy.app.timers.is_registered(_retry_start):
                    bpy.app.timers.register(_retry_start, persistent=True)
                return "retrying"
            return "listening"

        start()
        """;
}
