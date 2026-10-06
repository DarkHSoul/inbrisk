using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;

namespace Inbrisk.Mcp.DynamicToolset;

/// <summary>
/// Event arguments provided when the effective visible toolset changes.
/// </summary>
public sealed class ToolsListChangedEventArgs : EventArgs
{
    public IReadOnlySet<string> VisibleTools { get; }
    public string NotificationMethod => DynamicToolsetManager.NotificationMethod;

    public ToolsListChangedEventArgs(IReadOnlySet<string> visibleTools)
    {
        VisibleTools = visibleTools;
    }
}

/// <summary>
/// Represents an in-flight tool call token.
/// Ensures that an ongoing tool call started while a toolset was active remains
/// permitted and safe to complete execution even if focus switches and the toolset
/// deactivates midway through the call.
/// </summary>
public sealed class InFlightCallToken : IDisposable
{
    private readonly DynamicToolsetManager _manager;
    private int _disposed;

    public string ToolName { get; }
    public string? ToolsetName { get; }
    public bool WasPermittedAtStart { get; }
    public DateTimeOffset StartedAt { get; }

    internal InFlightCallToken(DynamicToolsetManager manager, string toolName, string? toolsetName, bool permitted)
    {
        _manager = manager;
        ToolName = toolName;
        ToolsetName = toolsetName;
        WasPermittedAtStart = permitted;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// True if the call was valid when initiated and has not yet completed (disposed).
    /// </summary>
    public bool IsValid => WasPermittedAtStart && Volatile.Read(ref _disposed) == 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _manager.ReleaseInFlightCall(this);
        }
    }
}

/// <summary>
/// Manages dynamic toolsets and MCP notifications/tools/list_changed.
/// Maintains a small stable core profile (~10-12 primitive tools), activates contextual toolsets
/// (e.g. browser tools) when relevant foreground applications gain focus, provides hysteresis/debounce
/// on exit to prevent flapping, supports explicit pinning across focus changes, and guarantees
/// session isolation and in-flight call safety.
/// </summary>
public sealed class DynamicToolsetManager : IDisposable
{
    public const string NotificationMethod = "notifications/tools/list_changed";

    // ---------------- Core & Optional Toolset Definitions ----------------

    /// <summary>
    /// Small stable core profile consisting of 16 fundamental desktop primitive tools.
    /// Always visible unless explicitly replaced by custom profiles.
    /// Exactly zero overlap with contextual toolsets.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultCoreTools = McpHost.CoreTools;

    /// <summary>
    /// Contextual browser toolset activated when a browser process is foregrounded.
    /// </summary>
    public static readonly IReadOnlySet<string> BrowserToolset = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "browser_browse",
        "browser_click",
        "browser_type",
        "browser_snapshot",
        "browser_tabs",
        "browser_content",
        "browser_screenshot"
    };

    /// <summary>
    /// Registry of known optional contextual toolsets.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> KnownToolsets =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["browser"] = BrowserToolset,
        };

    private static readonly HashSet<string> KnownBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "chrome.exe",
        "msedge", "msedge.exe",
        "edge", "edge.exe",
        "firefox", "firefox.exe",
        "brave", "brave.exe",
        "opera", "opera.exe",
        "vivaldi", "vivaldi.exe",
        "arc", "arc.exe"
    };

    // ---------------- Instance State ----------------

    private readonly object _stateLock = new();
    private readonly HashSet<string> _pinnedToolsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _contextualActiveToolsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<InFlightCallToken, byte> _inFlightCalls = new();
    private HashSet<string> _lastEmittedTools;
    private DateTimeOffset? _deactivationPendingSince;
    private string? _currentForegroundProcess;
    private bool _disposed;

    /// <summary>
    /// Base profile name ("core" or "full").
    /// </summary>
    public string BaseProfile { get; set; } = "core";

    /// <summary>
    /// When true, clients without dynamic tool listing capability receive a deterministic
    /// usable toolset containing core and standard contextual tools without dynamic deactivation.
    /// </summary>
    public bool CompatibilityMode { get; set; }

    /// <summary>
    /// Debounce duration for contextual deactivations (hysteresis to prevent flapping on quick switches).
    /// </summary>
    public TimeSpan DebounceDuration { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Configurable time provider for testability.
    /// </summary>
    public Func<DateTimeOffset> TimeProvider { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Invoked whenever the effective visible toolset actually changes.
    /// Never invoked on no-op recalculations.
    /// </summary>
    public event EventHandler<ToolsListChangedEventArgs>? ToolsListChanged;

    /// <summary>
    /// Optional async notification hook for MCP transport emission.
    /// </summary>
    public Func<IReadOnlySet<string>, Task>? NotificationSenderAsync { get; set; }

    /// <summary>
    /// Counter tracking emitted list_changed notifications.
    /// </summary>
    public int ListChangedNotificationsCount { get; private set; }

    /// <summary>
    /// Active in-flight call count.
    /// </summary>
    public int ActiveInFlightCallsCount => _inFlightCalls.Count;

    public DynamicToolsetManager(string baseProfile = "core", bool compatibilityMode = false)
    {
        BaseProfile = baseProfile;
        CompatibilityMode = compatibilityMode;
        _lastEmittedTools = new HashSet<string>(ComputeEffectiveToolsInternal(), StringComparer.OrdinalIgnoreCase);
    }

    // ---------------- Browser Detection ----------------

    /// <summary>
    /// Evaluates whether a process name or window title represents a supported web browser.
    /// </summary>
    public static bool IsBrowserProcess(string? processName, string? windowTitle = null)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        var name = Path.GetFileNameWithoutExtension(processName).Trim();
        return KnownBrowserProcesses.Contains(name) || KnownBrowserProcesses.Contains(processName.Trim());
    }

    // ---------------- Context & Focus Handling ----------------

    /// <summary>
    /// Updates foreground window context. Activates browser toolset immediately if browser is foregrounded;
    /// applies debounce/hysteresis before deactivating unpinned toolsets on exit.
    /// </summary>
    public bool UpdateForegroundContext(string? processName, string? windowTitle = null, DateTimeOffset? now = null)
    {
        lock (_stateLock)
        {
            if (_disposed) return false;

            _currentForegroundProcess = processName;
            var currentTime = now ?? TimeProvider();
            var isBrowser = IsBrowserProcess(processName, windowTitle);

            if (isBrowser)
            {
                // Immediate activation on entering browser context; cancel any pending deactivation
                _deactivationPendingSince = null;
                _contextualActiveToolsets.Add("browser");
                return EvaluateEffectiveTools();
            }
            else
            {
                // Foreground switched away from browser
                if (_contextualActiveToolsets.Contains("browser"))
                {
                    if (DebounceDuration <= TimeSpan.Zero)
                    {
                        // Immediate deactivation if debounce is 0
                        _contextualActiveToolsets.Remove("browser");
                        _deactivationPendingSince = null;
                        return EvaluateEffectiveTools();
                    }

                    if (_deactivationPendingSince == null)
                    {
                        // Start hysteresis debounce window
                        _deactivationPendingSince = currentTime;
                        return false;
                    }

                    if (currentTime - _deactivationPendingSince.Value >= DebounceDuration)
                    {
                        // Debounce expired: commit deactivation
                        _contextualActiveToolsets.Remove("browser");
                        _deactivationPendingSince = null;
                        return EvaluateEffectiveTools();
                    }
                }
                return false;
            }
        }
    }

    /// <summary>
    /// Explicitly processes pending debounce timers. Allows callers or timers to flush deactivations.
    /// </summary>
    public bool ProcessPendingDebounce(DateTimeOffset? now = null, bool force = false)
    {
        lock (_stateLock)
        {
            if (_disposed || _deactivationPendingSince == null) return false;

            var currentTime = now ?? TimeProvider();
            if (force || (currentTime - _deactivationPendingSince.Value >= DebounceDuration))
            {
                _contextualActiveToolsets.Remove("browser");
                _deactivationPendingSince = null;
                return EvaluateEffectiveTools();
            }

            return false;
        }
    }

    // ---------------- Pinning ----------------

    /// <summary>
    /// Explicitly pins a toolset across focus changes.
    /// </summary>
    public bool PinToolset(string toolsetName)
    {
        if (string.IsNullOrWhiteSpace(toolsetName)) return false;

        lock (_stateLock)
        {
            if (_disposed) return false;

            var name = toolsetName.Trim().ToLowerInvariant();
            if (_pinnedToolsets.Add(name))
            {
                return EvaluateEffectiveTools();
            }
            return false;
        }
    }

    /// <summary>
    /// Unpins a toolset. If the toolset was only active due to pinning and context does not warrant it,
    /// it deactivates according to debounce policy.
    /// </summary>
    public bool UnpinToolset(string toolsetName)
    {
        if (string.IsNullOrWhiteSpace(toolsetName)) return false;

        lock (_stateLock)
        {
            if (_disposed) return false;

            var name = toolsetName.Trim().ToLowerInvariant();
            if (_pinnedToolsets.Remove(name))
            {
                // If foreground is not a browser, exit immediately or start debounce
                if (name == "browser" && !IsBrowserProcess(_currentForegroundProcess))
                {
                    _contextualActiveToolsets.Remove("browser");
                    _deactivationPendingSince = null;
                }
                return EvaluateEffectiveTools();
            }
            return false;
        }
    }

    public bool IsPinned(string toolsetName)
    {
        lock (_stateLock)
        {
            return _pinnedToolsets.Contains(toolsetName);
        }
    }

    public IReadOnlySet<string> PinnedToolsets
    {
        get
        {
            lock (_stateLock)
            {
                return new HashSet<string>(_pinnedToolsets, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IReadOnlySet<string> ActiveToolsets
    {
        get
        {
            lock (_stateLock)
            {
                var combined = new HashSet<string>(_contextualActiveToolsets, StringComparer.OrdinalIgnoreCase);
                foreach (var pin in _pinnedToolsets) combined.Add(pin);
                return combined;
            }
        }
    }

    // ---------------- In-Flight Call Tracking ----------------

    /// <summary>
    /// Begins tracking an in-flight tool call.
    /// If the tool is permitted at start, the returned token guarantees validity until disposal,
    /// even if focus switches and the toolset deactivates midway.
    /// </summary>
    public InFlightCallToken BeginToolCall(string toolName)
    {
        lock (_stateLock)
        {
            var isPermitted = IsToolVisibleInternal(toolName);
            var toolset = ResolveToolsetName(toolName);
            var token = new InFlightCallToken(this, toolName, toolset, isPermitted);
            if (token.IsValid)
            {
                _inFlightCalls.TryAdd(token, 0);
            }
            return token;
        }
    }

    /// <summary>
    /// Acquires an in-flight call token for an ongoing tool call.
    /// Alias for BeginToolCall.
    /// </summary>
    public InFlightCallToken AcquireInFlightCall(string toolName) => BeginToolCall(toolName);

    /// <summary>
    /// Checks whether a tool is currently permitted/visible.
    /// Alias for IsToolVisible.
    /// </summary>
    public bool IsToolPermitted(string toolName) => IsToolVisible(toolName);

    internal void ReleaseInFlightCall(InFlightCallToken token)
    {
        _inFlightCalls.TryRemove(token, out _);
    }

    /// <summary>
    /// Checks whether a tool is currently available for execution.
    /// If an active in-flight token is provided, returns true if the token was permitted at initiation,
    /// protecting in-flight calls against mid-call focus deactivations.
    /// </summary>
    public bool IsToolAvailable(string toolName, InFlightCallToken? inFlight = null)
    {
        if (inFlight != null && inFlight.IsValid &&
            string.Equals(inFlight.ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        lock (_stateLock)
        {
            return IsToolVisibleInternal(toolName);
        }
    }

    public bool IsToolVisible(string toolName)
    {
        lock (_stateLock)
        {
            return IsToolVisibleInternal(toolName);
        }
    }

    private bool IsToolVisibleInternal(string toolName)
    {
        var effective = ComputeEffectiveToolsInternal();
        return effective.Contains(toolName);
    }

    private static string? ResolveToolsetName(string toolName)
    {
        foreach (var (setName, tools) in KnownToolsets)
        {
            if (tools.Contains(toolName)) return setName;
        }
        return null;
    }

    // ---------------- Visible Toolset Resolution & Notifications ----------------

    /// <summary>
    /// Returns the current effective visible tool names for this session.
    /// </summary>
    public IReadOnlySet<string> GetVisibleToolNames()
    {
        lock (_stateLock)
        {
            return ComputeEffectiveToolsInternal();
        }
    }

    private HashSet<string> ComputeEffectiveToolsInternal()
    {
        if (CompatibilityMode)
        {
            // Compatibility mode: return deterministic usable toolset (Core + Known contextual tools)
            var compat = new HashSet<string>(DefaultCoreTools, StringComparer.OrdinalIgnoreCase);
            foreach (var (_, tools) in KnownToolsets)
            {
                foreach (var t in tools) compat.Add(t);
            }
            return compat;
        }

        if (string.Equals(BaseProfile, "full", StringComparison.OrdinalIgnoreCase))
        {
            var full = new HashSet<string>(DefaultCoreTools, StringComparer.OrdinalIgnoreCase);
            foreach (var (_, tools) in KnownToolsets)
            {
                foreach (var t in tools) full.Add(t);
            }
            return full;
        }

        // Standard dynamic core: Core tools + active (contextual or pinned) toolsets
        var result = new HashSet<string>(DefaultCoreTools, StringComparer.OrdinalIgnoreCase);

        foreach (var pin in _pinnedToolsets)
        {
            if (KnownToolsets.TryGetValue(pin, out var pinTools))
            {
                foreach (var t in pinTools) result.Add(t);
            }
        }

        foreach (var act in _contextualActiveToolsets)
        {
            if (KnownToolsets.TryGetValue(act, out var actTools))
            {
                foreach (var t in actTools) result.Add(t);
            }
        }

        return result;
    }

    private bool EvaluateEffectiveTools()
    {
        var currentEffective = ComputeEffectiveToolsInternal();

        // No-op recalculation check: emit notification ONLY when effective visible toolset actually changes
        if (_lastEmittedTools.SetEquals(currentEffective))
        {
            return false;
        }

        _lastEmittedTools = new HashSet<string>(currentEffective, StringComparer.OrdinalIgnoreCase);
        ListChangedNotificationsCount++;

        var args = new ToolsListChangedEventArgs(_lastEmittedTools);
        ToolsListChanged?.Invoke(this, args);

        if (NotificationSenderAsync != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await NotificationSenderAsync(_lastEmittedTools).ConfigureAwait(false);
                }
                catch
                {
                    // Swallowing transport notification errors to prevent crashing background runner
                }
            });
        }

        return true;
    }

    // ---------------- Tool Filtering & Annotation Preservation ----------------

    /// <summary>
    /// Filters tools according to the current visible toolset and preserves/attaches
    /// correct ReadOnlyHint, IdempotentHint, and DestructiveHint annotations.
    /// </summary>
    public IList<Tool> FilterAndAnnotateTools(IEnumerable<Tool> sourceTools)
    {
        var visibleNames = GetVisibleToolNames();
        var filtered = sourceTools.Where(t => visibleNames.Contains(t.Name)).ToList();
        return ApplyAnnotations(filtered);
    }

    /// <summary>
    /// Preserves and applies standard MCP tool annotations (ReadOnlyHint, IdempotentHint, DestructiveHint).
    /// </summary>
    public static IList<Tool> ApplyAnnotations(IEnumerable<Tool> tools)
    {
        var list = tools.ToList();
        foreach (var t in list)
        {
            var name = t.Name;

            var isReadOnly = name is "computer_observe" or "computer_windows" or "computer_find"
                or "computer_inspect" or "computer_read" or "computer_screenshot" or "computer_capabilities"
                or "browser_snapshot" or "browser_screenshot" or "computer_screen_memory" or "computer_ui_status"
                or "computer_leases_status" or "computer_list_adapters" or "computer_apps"
                or "computer_app_status" or "computer_list_recipes" or "browser_tabs" or "browser_content";

            var isDestructive = name is "computer_batch" or "computer_do" or "computer_run"
                or "computer_click" or "computer_type" or "computer_hotkey"
                or "computer_close_window" or "browser_click" or "browser_type"
                or "computer_app_shutdown" or "computer_app_restart" or "computer_cancel_task"
                or "computer_cleanup";

            var isIdempotent = name is "computer_windows" or "computer_observe" or "computer_find"
                or "computer_inspect" or "computer_read" or "computer_screenshot" or "computer_capabilities"
                or "browser_snapshot" or "computer_reset_input" or "computer_screen_memory"
                or "computer_ui_status" or "computer_leases_status" or "browser_content"
                or "computer_wait" or "computer_wait_for" or "computer_wait_for_stable"
                or "computer_apps" or "computer_app_status" or "computer_list_recipes"
                or "computer_list_adapters" or "browser_tabs" or "browser_screenshot" or "computer_toolset";

            t.Annotations = new ToolAnnotations
            {
                ReadOnlyHint = isReadOnly ? true : null,
                IdempotentHint = isIdempotent ? true : null,
                DestructiveHint = isDestructive ? true : null,
            };
        }
        return list;
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _inFlightCalls.Clear();
            _pinnedToolsets.Clear();
            _contextualActiveToolsets.Clear();
        }
    }
}
