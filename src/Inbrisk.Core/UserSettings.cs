using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inbrisk.Core;

/// <summary>
/// User-facing preferences persisted at %LOCALAPPDATA%\inbrisk\settings.json
/// (INBRISK_DATA_DIR overrides the directory). Written by the setup app and
/// read by every new process: MCP sessions pick up hotkey/palette changes on
/// the next spawn — no restart orchestration needed.
/// </summary>
public sealed class UserSettings
{
    /// <summary>Panic chord, e.g. "Ctrl+Alt+Pause". Null = built-in default.</summary>
    public string? PanicHotkey { get; set; }
    /// <summary>Resume chord. Null = built-in default.</summary>
    public string? ResumeHotkey { get; set; }
    /// <summary>Idle/active glow color, "#RRGGBB". Null = default lime.</summary>
    public string? IdleColor { get; set; }
    /// <summary>Emergency-stop glow color, "#RRGGBB". Null = default red.</summary>
    public string? EmergencyColor { get; set; }
    /// <summary>Setup app theme: "system" | "dark" | "light".</summary>
    public string? Theme { get; set; }
    /// <summary>Update feed (manifest URL or file path) remembered by the app.</summary>
    public string? UpdateManifest { get; set; }

    /// <summary>Floating Activity Pill / HUD enabled. Default true.</summary>
    public bool HudEnabled { get; set; } = true;
    /// <summary>Perimeter smoke indicator enabled. Default true.</summary>
    public bool PerimeterEnabled { get; set; } = true;
    /// <summary>Bobbing and smooth HUD animations enabled. Default true.</summary>
    public bool AnimationsEnabled { get; set; } = true;
    /// <summary>HUD display position, e.g. "top-center".</summary>
    public string HudPosition { get; set; } = "top-center";
    /// <summary>Start Inbrisk automatically with Windows.</summary>
    public bool StartWithWindows { get; set; } = false;
    /// <summary>Start Inbrisk minimized to tray icon.</summary>
    public bool StartMinimized { get; set; } = true;
    /// <summary>Floating HUD always visible (shows Standby when idle vs only appearing during active actions).</summary>
    public bool HudAlwaysVisible { get; set; } = true;
    /// <summary>Dock gap in DIPs between the pill's right edge and the system tray's left edge.
    /// -1 = auto (default snug dock). Set by dragging the pill along the taskbar.</summary>
    public int HudDockGapDip { get; set; } = -1;
    /// <summary>Default MCP output verbosity: "slim" | "full". Null = full.
    /// "slim" compacts the heavy tool results (find/observe/run/capabilities)
    /// for small-context models; any tool call can override with detail.</summary>
    public string? OutputDetail { get; set; } = "slim";
    /// <summary>Tool profile: "core" (streamlined essential tools for fast reasoning) | "full" (all tools). Default "core".</summary>
    public string? ToolProfile { get; set; } = "core";
    /// <summary>
    /// Permit computer_launch path:/executable: launches of script and
    /// installer files (.bat/.cmd/.ps1/.vbs/.js/.wsf/.msi/.hta/.reg/…).
    /// Default false — such targets fail with ConfirmationRequired.
    /// INBRISK_ALLOW_SCRIPT_LAUNCH=1 grants the same consent per process.
    /// Human-edited only; the MCP surface never writes this setting.
    /// </summary>
    public bool AllowScriptLaunch { get; set; } = false;
    /// <summary>
    /// Processes protected from AI close/kill/termination actions.
    /// Immutable to AI; only editable by the human user via settings.
    /// </summary>
    public List<string> ProtectedProcesses { get; set; } = new()
    {
        "inbrisk.exe",
        "explorer.exe",
        "dwm.exe",
        "code.exe",
        "chrome.exe",
        "discord.exe",
        "obs64.exe",
        "windowsterminal.exe",
        "conhost.exe",
        "powershell.exe",
        "pwsh.exe",
        "cmd.exe",
        "antigravity.exe",
        "cursor.exe",
        "devenv.exe",
        "claude.exe"
    };

    /// <summary>
    /// Protection policy mode: "Deny" (unconditionally reject AI close actions)
    /// or "Ask" (pause and request human takeover/confirmation). Default: "Deny".
    /// </summary>
    public string ProtectionMode { get; set; } = "Deny";

    /// <summary>
    /// Local consent for caller-supplied script execution — browser_evaluate
    /// and adapter script-exec actions (CDP Runtime.evaluate, Blender bpy
    /// exec/eval). Dangerous class, default OFF: like the F04 shell-typing
    /// gate, this can only be granted by a local channel the model cannot
    /// forge — an edit to this file (setup app or manual) or the
    /// INBRISK_ALLOW_SCRIPT_EXECUTION env var on the host process, which
    /// wins both ways (truthy allows, set-but-falsy force-denies).
    /// AutoConfirm and tool-call flags are never consulted. Every attempt,
    /// allowed or denied, is appended to the hash-chained audit log
    /// (mcp-scriptexec.jsonl).
    /// </summary>
    public bool AllowScriptExecution { get; set; } = false;

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    [JsonIgnore]
    public static string DataDir =>
        Environment.GetEnvironmentVariable("INBRISK_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "inbrisk");

    private static UserSettings? _cachedSettings;
    private static string? _cachedPath;
    private static DateTime _cacheTimestamp = DateTime.MinValue;
    private static readonly object _cacheGate = new();
    private static FileSystemWatcher? _watcher;
    private static readonly object _watcherGate = new();

    public static void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cachedSettings = null;
            _cachedPath = null;
            _cacheTimestamp = DateTime.MinValue;
        }
    }

    public static void EnsureWatcher(string? dir = null, string? fileName = null)
    {
        lock (_watcherGate)
        {
            if (_watcher != null) return;
            try
            {
                var targetDir = dir ?? DataDir;
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                var targetFile = fileName ?? Path.GetFileName(SettingsPath);
                var watcher = new FileSystemWatcher(targetDir, targetFile)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                FileSystemEventHandler handler = (s, e) => InvalidateCache();
                RenamedEventHandler renamedHandler = (s, e) => InvalidateCache();

                watcher.Changed += handler;
                watcher.Created += handler;
                watcher.Deleted += handler;
                watcher.Renamed += renamedHandler;

                _watcher = watcher;
            }
            catch
            {
                // FileSystemWatcher is best-effort; 2s safety TTL serves as backup
            }
        }
    }

    public static void DisposeWatcher()
    {
        lock (_watcherGate)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    public static bool TryLoad(string? path, out UserSettings settings)
    {
        try
        {
            var targetPath = path ?? SettingsPath;
            if (!File.Exists(targetPath))
            {
                settings = new UserSettings();
                return true;
            }
            var text = File.ReadAllText(targetPath);
            var parsed = JsonSerializer.Deserialize<UserSettings>(text);
            if (parsed != null)
            {
                settings = parsed;
                return true;
            }
        }
        catch
        {
            // Transient error (e.g. file lock or partial write)
        }
        settings = new UserSettings();
        return false;
    }

    public static UserSettings LoadCached(TimeSpan? ttl = null, string? explicitPath = null)
    {
        EnsureWatcher();
        var effTtl = ttl ?? TimeSpan.FromSeconds(2);
        var now = DateTime.UtcNow;
        var targetPath = explicitPath ?? SettingsPath;
        lock (_cacheGate)
        {
            if (_cachedSettings != null && string.Equals(_cachedPath, targetPath, StringComparison.OrdinalIgnoreCase) && (now - _cacheTimestamp) < effTtl)
                return _cachedSettings;

            if (TryLoad(explicitPath, out var loaded))
            {
                _cachedSettings = loaded;
                _cachedPath = targetPath;
                _cacheTimestamp = now;
                return _cachedSettings;
            }

            // Transient failure: if we already have cached settings for this path, keep them to avoid poisoning
            if (_cachedSettings != null && string.Equals(_cachedPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedSettings;
            }

            _cachedSettings = loaded;
            _cachedPath = targetPath;
            _cacheTimestamp = now;
            return _cachedSettings;
        }
    }

    public static UserSettings Load(string? path = null)
    {
        TryLoad(path, out var s);
        return s;
    }

    public void Save(string? path = null)
    {
        try
        {
            var targetPath = path ?? SettingsPath;
            var dir = Path.GetDirectoryName(targetPath) ?? DataDir;
            Directory.CreateDirectory(dir);
            var tmp = targetPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, targetPath, overwrite: true);
            lock (_cacheGate)
            {
                _cachedSettings = this;
                _cachedPath = targetPath;
                _cacheTimestamp = DateTime.UtcNow;
            }
        }
        catch { /* preferences must never break a session */ }
    }

    /// <summary>Parse "#RRGGBB" or "RRGGBB" → RGB bytes, or null.</summary>
    public static (byte R, byte G, byte B)? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6) return null;
        try
        {
            return ((byte)Convert.ToInt32(h[..2], 16),
                    (byte)Convert.ToInt32(h[2..4], 16),
                    (byte)Convert.ToInt32(h[4..6], 16));
        }
        catch { return null; }
    }
}
