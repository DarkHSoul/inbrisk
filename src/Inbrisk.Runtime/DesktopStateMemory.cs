using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

public enum ElementPresence
{
    CurrentlyVisible,
    PreviouslySeen,
    StaleRecheckRequired,
}

public sealed record KnownScreenElement(
    string StableKey,
    string? Name,
    string Role,
    string? AutomationId,
    string ScreenId,
    string AppId,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    ElementPresence Presence);

public sealed record ScreenTransition(
    string FromScreenId,
    string ToScreenId,
    string AppId,
    string ActionDescription,
    string? TargetName = null,
    string? TargetRole = null,
    int SuccessCount = 1,
    DateTimeOffset LastUsed = default);

public sealed record AppScreen(
    string ScreenId,
    string AppId,
    string TitlePattern,
    IReadOnlyList<string> Landmarks,
    DateTimeOffset LastActive);

public sealed record NavigationSuggestion(
    string TargetScreen,
    string CurrentScreen,
    string TimeAgo,
    string NavigationHint,
    IReadOnlyList<string> Path);

/// <summary>
/// Persistent state and navigation memory for Windows applications:
/// - Recognizes active app screens/views from window titles, selected tabs, and landmarks
/// - Tracks element lifecycle across screens (CurrentlyVisible vs PreviouslySeen vs Stale)
/// - Learns screen transition graph from executed actions (Screen A -> Screen B via Action)
/// - Generates navigation hints when elements are not found in the current view
/// - Persists across sessions in %LOCALAPPDATA%/inbrisk/desktop-state-memory.json
/// </summary>
public sealed class DesktopStateMemory
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, AppScreen> _screens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _currentScreens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, KnownScreenElement> _knownElements = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ScreenTransition> _transitions = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _diskLock = new();
    private long _lastFlush;
    private bool _dirty;

    public DesktopStateMemory(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetEnvironmentVariable("INBRISK_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inbrisk"),
            "desktop-state-memory.json");

        LoadFromDisk();
    }

    // ---------------------------- Screen Inference ----------------------------

    /// <summary>
    /// Infers the semantic screen name from active controls (selected tabs/radio buttons)
    /// and the window title.
    /// </summary>
    public static string InferScreenName(string appId, string windowTitle,
        IEnumerable<(string Role, string Name, bool IsSelected)> controls)
    {
        // 1. Look for an explicitly selected TabItem, RadioButton, or nav button
        foreach (var c in controls)
        {
            if (c.IsSelected && !string.IsNullOrWhiteSpace(c.Name))
            {
                var role = c.Role;
                if (role is "TabItem" or "Tab" or "RadioButton" or "ListItem" or "Button")
                    return c.Name.Trim();
            }
        }

        // 2. Check window title for subscreen delimiters (e.g. "Spotify - Kitaplığın" or "Settings - App")
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            var cleanTitle = windowTitle.Trim();
            var parts = cleanTitle.Split(new[] { " - ", " – ", " — ", " | ", ": " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                if (parts[0].Equals(appId, StringComparison.OrdinalIgnoreCase))
                    return parts[1].Trim();
                if (parts[^1].Equals(appId, StringComparison.OrdinalIgnoreCase))
                    return parts[0].Trim();
                return parts[0].Trim();
            }
            return cleanTitle;
        }

        return "Main";
    }

    public string InferScreen(string appId, string windowTitle, IEnumerable<ObsElement> elements)
    {
        var controls = elements.Select(e => (
            e.Role,
            e.Name ?? "",
            IsSelected: e.State?.Contains("selected", StringComparison.OrdinalIgnoreCase) == true
                     || e.State?.Contains("checked", StringComparison.OrdinalIgnoreCase) == true
                     || e.State?.Contains("focused", StringComparison.OrdinalIgnoreCase) == true
        )).ToList();

        return InferScreenName(appId, windowTitle, controls);
    }

    public string InferScreen(string appId, string windowTitle, IEnumerable<UiElement> elements)
    {
        var controls = elements.Select(e => (
            e.Role.ToString(),
            e.Name ?? "",
            IsSelected: e.Props.TryGetValue("selected", out var s) && s is true
                     || e.Props.TryGetValue("checked", out var c) && c is true
                     || e.Props.TryGetValue("focused", out var f) && f is true
        )).ToList();

        return InferScreenName(appId, windowTitle, controls);
    }

    // ---------------------------- Observations ----------------------------

    public string RecordObservation(long hwnd, string appId, string windowTitle, IEnumerable<ObsElement> elements)
    {
        if (string.IsNullOrWhiteSpace(appId)) appId = "desktop";
        var appKey = appId.ToLowerInvariant();
        var elList = elements.ToList();
        var screenId = InferScreen(appKey, windowTitle, elList);

        _currentScreens[appKey] = screenId;

        // Landmarks from interactive elements
        var landmarks = elList
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.Role is "Button" or "TabItem" or "Tab" or "Hyperlink" or "MenuItem")
            .Select(e => e.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        var screenKey = $"{appKey}:{screenId}";
        _screens[screenKey] = new AppScreen(screenId, appKey, windowTitle, landmarks, DateTimeOffset.Now);

        var now = DateTimeOffset.Now;
        // Update presence for currently visible elements
        foreach (var e in elList)
        {
            var key = e.StableKey;
            _knownElements.AddOrUpdate(key,
                k => new KnownScreenElement(k, e.Name, e.Role, null, screenId, appKey, now, now, ElementPresence.CurrentlyVisible),
                (k, existing) => existing with
                {
                    Name = e.Name ?? existing.Name,
                    Role = e.Role,
                    ScreenId = screenId,
                    LastSeen = now,
                    Presence = ElementPresence.CurrentlyVisible,
                });
        }

        // Mark elements on other screens of this app as PreviouslySeen
        foreach (var kv in _knownElements)
        {
            if (kv.Value.AppId.Equals(appKey, StringComparison.OrdinalIgnoreCase) &&
                !kv.Value.ScreenId.Equals(screenId, StringComparison.OrdinalIgnoreCase) &&
                kv.Value.Presence == ElementPresence.CurrentlyVisible)
            {
                _knownElements[kv.Key] = kv.Value with { Presence = ElementPresence.PreviouslySeen };
            }
        }

        _dirty = true;
        FlushThrottled();
        return screenId;
    }

    public string RecordObservation(long hwnd, string appId, string windowTitle, IEnumerable<UiElement> elements)
    {
        if (string.IsNullOrWhiteSpace(appId)) appId = "desktop";
        var appKey = appId.ToLowerInvariant();
        var elList = elements.ToList();
        var screenId = InferScreen(appKey, windowTitle, elList);

        _currentScreens[appKey] = screenId;

        var landmarks = elList
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.Role is Role.Button or Role.TabItem or Role.Tab or Role.Hyperlink or Role.MenuItem)
            .Select(e => e.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        var screenKey = $"{appKey}:{screenId}";
        _screens[screenKey] = new AppScreen(screenId, appKey, windowTitle, landmarks, DateTimeOffset.Now);

        var now = DateTimeOffset.Now;
        foreach (var e in elList)
        {
            var key = ObservationBuilder.StableKey(e, e.Handle?.Recipe?.AutomationId);
            _knownElements.AddOrUpdate(key,
                k => new KnownScreenElement(k, e.Name, e.Role.ToString(), e.Handle?.Recipe?.AutomationId, screenId, appKey, now, now, ElementPresence.CurrentlyVisible),
                (k, existing) => existing with
                {
                    Name = e.Name ?? existing.Name,
                    Role = e.Role.ToString(),
                    ScreenId = screenId,
                    LastSeen = now,
                    Presence = ElementPresence.CurrentlyVisible,
                });
        }

        foreach (var kv in _knownElements)
        {
            if (kv.Value.AppId.Equals(appKey, StringComparison.OrdinalIgnoreCase) &&
                !kv.Value.ScreenId.Equals(screenId, StringComparison.OrdinalIgnoreCase) &&
                kv.Value.Presence == ElementPresence.CurrentlyVisible)
            {
                _knownElements[kv.Key] = kv.Value with { Presence = ElementPresence.PreviouslySeen };
            }
        }

        _dirty = true;
        FlushThrottled();
        return screenId;
    }

    // ---------------------------- Transitions ----------------------------

    public void RecordTransition(string appId, string fromScreen, string toScreen, string actionDescription,
        string? targetName = null, string? targetRole = null)
    {
        if (string.IsNullOrWhiteSpace(appId)) appId = "desktop";
        var appKey = appId.ToLowerInvariant();

        if (string.Equals(fromScreen, toScreen, StringComparison.OrdinalIgnoreCase))
            return;

        var transKey = $"{appKey}:{fromScreen}->{toScreen}:{actionDescription}";
        var now = DateTimeOffset.Now;

        _transitions.AddOrUpdate(transKey,
            k => new ScreenTransition(fromScreen, toScreen, appKey, actionDescription, targetName, targetRole, 1, now),
            (k, existing) => existing with
            {
                SuccessCount = existing.SuccessCount + 1,
                LastUsed = now,
            });

        _dirty = true;
        FlushThrottled();
    }

    // ---------------------------- Navigation Path Search ----------------------------

    public List<ScreenTransition>? FindNavigationPath(string appId, string fromScreen, string toScreen)
    {
        if (string.Equals(fromScreen, toScreen, StringComparison.OrdinalIgnoreCase))
            return [];

        var appKey = appId.ToLowerInvariant();
        var transitions = _transitions.Values
            .Where(t => t.AppId.Equals(appKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var queue = new Queue<List<ScreenTransition>>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fromScreen };

        foreach (var t in transitions.Where(t => t.FromScreenId.Equals(fromScreen, StringComparison.OrdinalIgnoreCase)))
        {
            if (t.ToScreenId.Equals(toScreen, StringComparison.OrdinalIgnoreCase))
                return [t];

            visited.Add(t.ToScreenId);
            queue.Enqueue([t]);
        }

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var current = path[^1].ToScreenId;

            if (path.Count >= 5) continue; // bound search depth

            foreach (var t in transitions.Where(t => t.FromScreenId.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                if (t.ToScreenId.Equals(toScreen, StringComparison.OrdinalIgnoreCase))
                {
                    var full = new List<ScreenTransition>(path) { t };
                    return full;
                }

                if (visited.Add(t.ToScreenId))
                {
                    var nextPath = new List<ScreenTransition>(path) { t };
                    queue.Enqueue(nextPath);
                }
            }
        }

        return null;
    }

    // ---------------------------- Recovery Suggestions ----------------------------

    public NavigationSuggestion? SuggestRecovery(string? targetName, string? targetRole, string appId, string? currentScreen = null)
    {
        if (string.IsNullOrEmpty(targetName) && string.IsNullOrEmpty(targetRole))
            return null;

        var appKey = appId.ToLowerInvariant();
        currentScreen ??= GetCurrentScreen(appKey);

        var candidates = _knownElements.Values
            .Where(e => e.AppId.Equals(appKey, StringComparison.OrdinalIgnoreCase) &&
                        !e.ScreenId.Equals(currentScreen, StringComparison.OrdinalIgnoreCase))
            .Where(e =>
            {
                if (!string.IsNullOrEmpty(targetName))
                {
                    if (e.Name == null || !e.Name.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                if (!string.IsNullOrEmpty(targetRole))
                {
                    if (!e.Role.Equals(targetRole, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            })
            .OrderByDescending(e => e.LastSeen)
            .ToList();

        if (candidates.Count == 0) return null;

        var best = candidates[0];
        var elapsed = DateTimeOffset.Now - best.LastSeen;
        var timeAgo = elapsed.TotalSeconds < 60
            ? $"{(int)Math.Max(1, elapsed.TotalSeconds)}s ago"
            : $"{(int)elapsed.TotalMinutes}m ago";

        var path = FindNavigationPath(appKey, currentScreen, best.ScreenId);
        string hint;
        List<string> pathDescs;
        if (path != null && path.Count > 0)
        {
            pathDescs = path.Select(p => p.ActionDescription).ToList();
            hint = string.Join(" → ", pathDescs);
        }
        else
        {
            pathDescs = [];
            hint = $"Navigate to screen '{best.ScreenId}'";
        }

        return new NavigationSuggestion(best.ScreenId, currentScreen, timeAgo, hint, pathDescs);
    }

    // ---------------------------- Queries & Summaries ----------------------------

    public string GetCurrentScreen(string appId) =>
        _currentScreens.GetValueOrDefault(appId.ToLowerInvariant(), "Main");

    public IReadOnlyList<AppScreen> GetScreens(string appId)
    {
        var appKey = appId.ToLowerInvariant();
        return _screens.Values.Where(s => s.AppId.Equals(appKey, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public IReadOnlyList<KnownScreenElement> GetKnownElements(string appId, string? screenId = null)
    {
        var appKey = appId.ToLowerInvariant();
        return _knownElements.Values
            .Where(e => e.AppId.Equals(appKey, StringComparison.OrdinalIgnoreCase) &&
                        (screenId == null || e.ScreenId.Equals(screenId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public IReadOnlyList<ScreenTransition> GetTransitions(string appId)
    {
        var appKey = appId.ToLowerInvariant();
        return _transitions.Values
            .Where(t => t.AppId.Equals(appKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public Dictionary<string, object?> GetSummary(string? appId = null)
    {
        var summary = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(appId))
        {
            var appKey = appId.ToLowerInvariant();
            summary["app"] = appKey;
            summary["currentScreen"] = GetCurrentScreen(appKey);
            summary["screens"] = GetScreens(appKey).Select(s => new
            {
                id = s.ScreenId,
                title = s.TitlePattern,
                landmarks = s.Landmarks,
                lastActive = s.LastActive,
            }).ToList();
            summary["transitions"] = GetTransitions(appKey).Select(t => new
            {
                from = t.FromScreenId,
                to = t.ToScreenId,
                action = t.ActionDescription,
                successes = t.SuccessCount,
            }).ToList();
            summary["knownElementsCount"] = GetKnownElements(appKey).Count;
        }
        else
        {
            summary["apps"] = _screens.Values.Select(s => s.AppId).Distinct().ToList();
            summary["totalScreens"] = _screens.Count;
            summary["totalTransitions"] = _transitions.Count;
            summary["totalKnownElements"] = _knownElements.Count;
        }
        return summary;
    }

    // ---------------------------- Persistence ----------------------------

    private sealed record PersistedState(
        List<AppScreen> Screens,
        Dictionary<string, string> CurrentScreens,
        List<KnownScreenElement> Elements,
        List<ScreenTransition> Transitions);

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<PersistedState>(json);
            if (state == null) return;

            foreach (var s in state.Screens ?? [])
                _screens[$"{s.AppId}:{s.ScreenId}"] = s;

            foreach (var kv in state.CurrentScreens ?? new())
                _currentScreens[kv.Key] = kv.Value;

            foreach (var e in state.Elements ?? [])
                _knownElements[e.StableKey] = e with { Presence = ElementPresence.PreviouslySeen };

            foreach (var t in state.Transitions ?? [])
                _transitions[$"{t.AppId}:{t.FromScreenId}->{t.ToScreenId}:{t.ActionDescription}"] = t;
        }
        catch { }
    }

    public void Flush()
    {
        lock (_diskLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Trim elements older than 7 days if memory grows large
                var cutoff = DateTimeOffset.Now.AddDays(-7);
                var activeElements = _knownElements.Values
                    .Where(e => e.LastSeen > cutoff)
                    .OrderByDescending(e => e.LastSeen)
                    .Take(1000)
                    .ToList();

                var state = new PersistedState(
                    _screens.Values.ToList(),
                    new Dictionary<string, string>(_currentScreens),
                    activeElements,
                    _transitions.Values.ToList());

                var tmp = _filePath + $".{Environment.ProcessId}.tmp";
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
                _dirty = false;
            }
            catch { }
        }
    }

    private void FlushThrottled()
    {
        var now = Environment.TickCount64;
        if (!_dirty || now - _lastFlush < 1000) return;
        _lastFlush = now;
        Task.Run(Flush);
    }
}
