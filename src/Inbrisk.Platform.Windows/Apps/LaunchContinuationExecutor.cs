using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Apps;

/// <summary>
/// Result of an application launch with optional continuation execution.
/// </summary>
public sealed record LaunchContinuationResult(
    string Status,
    bool Success,
    string? App,
    string? LaunchState,
    long? Hwnd,
    int? Pid,
    Dictionary<string, object?>? InitialMap,
    object? ContinuationOutcome,
    bool IsPaused,
    string? PauseReason,
    object? Modal,
    string? Note,
    string? Error = null);

/// <summary>
/// Executes application launches with optional continuation plans.
/// Features:
/// 1. Constrains deep links strictly to supported app protocols (blocks arbitrary shell commands).
/// 2. Integrates persistent profile knowledge into the compact launch app-map.
/// 3. Detects unexpected modals and returns a single meaningful pause rather than failing catastrophically.
/// 4. Reuses the canonical plan executor (RunPlanCore) to run post-launch continuation steps in a single roundtrip.
/// </summary>
public sealed class LaunchContinuationExecutor
{
    private readonly IAppService _appService;
    private readonly IWindowService _windowService;
    private readonly ApplicationProfileStore? _profileStore;
    private readonly Func<IReadOnlyList<object>, CancellationToken, Task<object?>>? _planExecutor;

    public LaunchContinuationExecutor(
        IAppService appService,
        IWindowService windowService,
        ApplicationProfileStore? profileStore = null,
        Func<IReadOnlyList<object>, CancellationToken, Task<object?>>? planExecutor = null)
    {
        _appService = appService ?? throw new ArgumentNullException(nameof(appService));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _profileStore = profileStore;
        _planExecutor = planExecutor;
    }

    /// <summary>
    /// Builds a compact application map combining UIA elements and persistent profile knowledge.
    /// </summary>
    public static Dictionary<string, object?> BuildCompactAppMap(
        long hwnd,
        int? pid,
        string? title,
        ApplicationProfile? profile = null,
        IReadOnlyList<UiElement>? discoveredElements = null,
        WindowInfo? modal = null)
    {
        var landmarks = new List<string>();

        // 1. Incorporate persistent profile knowledge
        if (profile?.Landmarks != null)
        {
            foreach (var lmName in profile.Landmarks.Keys)
            {
                if (!landmarks.Contains(lmName, StringComparer.OrdinalIgnoreCase))
                {
                    landmarks.Add(lmName);
                }
            }
        }

        // 2. Discover standard UIA structural landmarks
        if (discoveredElements != null)
        {
            var uiaLandmarks = discoveredElements
                .Where(e => e.Role is Role.TitleBar or Role.Menu or Role.Toolbar or Role.Tab)
                .Select(e => e.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct();

            foreach (var lm in uiaLandmarks)
            {
                if (!landmarks.Contains(lm, StringComparer.OrdinalIgnoreCase))
                {
                    landmarks.Add(lm);
                }
            }
        }

        // 3. Extract primary actionable controls
        var firstActionables = new List<object>();
        if (discoveredElements != null)
        {
            var prioritized = discoveredElements
                .Where(e => e.Actions.Count > 0 || e.Role is Role.Button or Role.Edit or Role.MenuItem or Role.TabItem or Role.CheckBox)
                .Take(12)
                .Select(e => new Dictionary<string, object?>
                {
                    ["id"] = e.Id,
                    ["role"] = e.Role.ToString(),
                    ["name"] = e.Name,
                    ["actions"] = e.Actions,
                    ["bounds"] = $"({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})"
                });
            firstActionables.AddRange(prioritized);
        }

        return new Dictionary<string, object?>
        {
            ["hwnd"] = $"0x{hwnd:X}",
            ["pid"] = pid,
            ["title"] = title,
            ["ready"] = true,
            ["landmarks"] = landmarks,
            ["firstActionables"] = firstActionables,
            ["modal"] = (modal != null && modal.Hwnd != hwnd) ? new { hwnd = $"0x{modal.Hwnd:X}", title = modal.Title } : null
        };
    }

    /// <summary>
    /// Executes the launch request and post-launch continuation steps.
    /// </summary>
    public async Task<LaunchContinuationResult> ExecuteLaunchWithContinuationAsync(
        LaunchSpec spec,
        IReadOnlyList<object>? thenSteps = null,
        Func<IReadOnlyList<object>, CancellationToken, Task<object?>>? planExecutorOverride = null,
        Func<long, CancellationToken, Task<IReadOnlyList<UiElement>>>? elementFinder = null,
        ApplicationProfile? explicitProfile = null,
        CancellationToken ct = default)
    {
        // 1. Constrain deep link protocol to safe application schemes
        if (!string.IsNullOrEmpty(spec.Uri))
        {
            DeepLinkSecurity.AssertSafeDeepLink(spec.Uri);
        }

        // 2. Launch application through AppService
        var launchResult = _appService.Launch(spec, ct);
        if (!launchResult.Success)
        {
            return new LaunchContinuationResult(
                Status: "Failed",
                Success: false,
                App: spec.App ?? spec.Executable ?? spec.Path ?? spec.Aumid ?? spec.Uri,
                LaunchState: launchResult.LaunchState,
                Hwnd: launchResult.Hwnd,
                Pid: launchResult.Pid,
                InitialMap: null,
                ContinuationOutcome: null,
                IsPaused: false,
                PauseReason: null,
                Modal: null,
                Note: null,
                Error: launchResult.ErrorDetail ?? launchResult.Error);
        }

        // 3. Inspect for unexpected modal dialogs
        WindowInfo? modal = null;
        if (launchResult.Hwnd.HasValue)
        {
            modal = _windowService.GetModalPopup(launchResult.Hwnd.Value);
        }
        if (modal == null && launchResult.Pid.HasValue)
        {
            var dialogs = _windowService.FindSystemDialogs();
            modal = dialogs.FirstOrDefault(d => d.Pid == launchResult.Pid.Value);
        }

        if (modal != null)
        {
            // Do not fail catastrophically: return a single meaningful pause!
            return new LaunchContinuationResult(
                Status: "Paused",
                Success: true,
                App: launchResult.ResolvedName ?? spec.App,
                LaunchState: launchResult.LaunchState,
                Hwnd: launchResult.Hwnd,
                Pid: launchResult.Pid,
                InitialMap: null,
                ContinuationOutcome: null,
                IsPaused: true,
                PauseReason: "UnexpectedModal",
                Modal: new { hwnd = $"0x{modal.Hwnd:X}", title = modal.Title },
                Note: "Application launched successfully, but an unexpected modal dialog appeared. Execution paused for resolution.",
                Error: null);
        }

        // 4. Resolve profile knowledge if available
        var profile = explicitProfile;
        if (profile == null && _profileStore != null && !string.IsNullOrEmpty(launchResult.ResolvedName))
        {
            try
            {
                var identity = new ApplicationIdentity(
                    displayName: launchResult.ResolvedName,
                    canonicalExecutablePath: launchResult.ResolvedIdentifier);
                profile = _profileStore.GetProfile(identity);
            }
            catch { }
        }

        // 5. Build compact application map
        IReadOnlyList<UiElement>? els = null;
        if (launchResult.Hwnd.HasValue && elementFinder != null)
        {
            try
            {
                els = await elementFinder(launchResult.Hwnd.Value, ct).ConfigureAwait(false);
            }
            catch { }
        }

        var initialMap = BuildCompactAppMap(
            launchResult.Hwnd ?? 0,
            launchResult.Pid,
            launchResult.WindowTitle,
            profile,
            els,
            modal);

        // 6. Execute continuation using the canonical plan executor
        object? continuationOutcome = null;
        if (thenSteps != null && thenSteps.Count > 0)
        {
            var executor = planExecutorOverride ?? _planExecutor;
            if (executor == null)
            {
                throw new InvalidOperationException("No canonical plan executor provided to execute post-launch continuation steps.");
            }
            continuationOutcome = await executor(thenSteps, ct).ConfigureAwait(false);
        }

        return new LaunchContinuationResult(
            Status: "Verified",
            Success: true,
            App: launchResult.ResolvedName ?? spec.App,
            LaunchState: launchResult.LaunchState,
            Hwnd: launchResult.Hwnd,
            Pid: launchResult.Pid,
            InitialMap: initialMap,
            ContinuationOutcome: continuationOutcome,
            IsPaused: false,
            PauseReason: null,
            Modal: null,
            Note: launchResult.LaunchState == "AlreadyRunning" ? "Reused running window" : null,
            Error: null);
    }
}
