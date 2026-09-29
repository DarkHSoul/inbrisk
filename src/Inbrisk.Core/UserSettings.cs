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
    /// <summary>Default MCP output verbosity: "slim" | "full". Null = full.
    /// "slim" compacts the heavy tool results (find/observe/run/capabilities)
    /// for small-context models; any tool call can override with detail.</summary>
    public string? OutputDetail { get; set; }
    /// <summary>Tool profile: "full" (all tools) | "core" (streamlined essential tools for fast reasoning). Default "full".</summary>
    public string? ToolProfile { get; set; }
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

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    [JsonIgnore]
    public static string DataDir =>
        Environment.GetEnvironmentVariable("INBRISK_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "inbrisk");

    private static UserSettings? _cachedSettings;
    private static DateTime _cacheTimestamp = DateTime.MinValue;
    private static readonly object _cacheGate = new();

    public static UserSettings LoadCached(TimeSpan? ttl = null)
    {
        var effTtl = ttl ?? TimeSpan.FromSeconds(2);
        var now = DateTime.UtcNow;
        lock (_cacheGate)
        {
            if (_cachedSettings != null && (now - _cacheTimestamp) < effTtl)
                return _cachedSettings;

            _cachedSettings = Load();
            _cacheTimestamp = now;
            return _cachedSettings;
        }
    }

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new UserSettings();
            return JsonSerializer.Deserialize<UserSettings>(
                File.ReadAllText(SettingsPath)) ?? new UserSettings();
        }
        catch { return new UserSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsPath, overwrite: true);
            lock (_cacheGate)
            {
                _cachedSettings = this;
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
