using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Platform.Windows;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;

namespace Inbrisk.Mcp;

/// <summary>
/// Session coordinator for isolated Ghost desktop session execution.
/// Unifies headless loopback RDP, GhostControl daemon IPC, and local Ghost Desktop operations.
/// </summary>
public sealed class GhostSessionCoordinator : IDisposable
{
    private readonly McpSession _session;
    private readonly Lazy<GhostMcpBridge> _bridgeLazy;
    private int _disposed;

    /// <summary>
    /// Underlying GhostMcpBridge instance.
    /// </summary>
    public GhostMcpBridge Bridge => _bridgeLazy.Value;

    public GhostSessionCoordinator(McpSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _bridgeLazy = new Lazy<GhostMcpBridge>(() => new GhostMcpBridge(session));
    }

    /// <summary>
    /// Routes application launch to the Ghost Desktop.
    /// </summary>
    public async Task<CallToolResult> LaunchAsync(
        string? app,
        string? search,
        string? executable,
        string? path,
        string? aumid,
        string? uri,
        string[]? arguments,
        string? workingDir = null,
        CancellationToken ct = default)
    {
        if (EmergencyGate.IsStopped)
        {
            return EmergencyResult();
        }

        string? targetProg = path ?? executable;
        if (string.IsNullOrWhiteSpace(targetProg))
        {
            var query = app ?? search;
            if (!string.IsNullOrWhiteSpace(query))
            {
                try
                {
                    var exeHits = _session.Rt.Parts.Apps.FindExecutables(query);
                    if (exeHits.Count > 0)
                    {
                        targetProg = exeHits[0];
                    }
                    else
                    {
                        var candidates = _session.Rt.Parts.Apps.ResolveCandidates(query);
                        if (candidates.Count > 0 && !string.IsNullOrWhiteSpace(candidates[0].Identifier))
                        {
                            targetProg = candidates[0].Identifier;
                        }
                    }
                }
                catch { }

                targetProg ??= query;
            }
        }

        if (string.IsNullOrWhiteSpace(targetProg))
        {
            targetProg = aumid ?? uri;
        }

        if (string.IsNullOrWhiteSpace(targetProg))
        {
            return ErrorResult("Malformed", "No valid application identifier provided for ghost launch.");
        }

        string? argsStr = arguments != null && arguments.Length > 0
            ? string.Join(" ", arguments)
            : null;

        return await Bridge.GhostDesktopLaunch(targetProg, argsStr, workingDir, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes multi-step computer_do actions to the Ghost Desktop.
    /// </summary>
    public async Task<CallToolResult> ComputerDoAsync(
        string? app,
        string? click,
        string? type,
        string? hotkey,
        bool submit,
        InbriskTools.TargetSpec? target,
        int waitMs,
        CancellationToken ct = default)
    {
        if (EmergencyGate.IsStopped)
        {
            return EmergencyResult();
        }

        var steps = new List<InbriskTools.BatchStep>();
        if (!string.IsNullOrWhiteSpace(app))
        {
            steps.Add(new InbriskTools.BatchStep { Action = "launch", T = app });
        }
        if (!string.IsNullOrWhiteSpace(click) || target != null)
        {
            steps.Add(new InbriskTools.BatchStep
            {
                Action = "click",
                T = target?.Name ?? click
            });
        }
        if (!string.IsNullOrWhiteSpace(type))
        {
            steps.Add(new InbriskTools.BatchStep
            {
                Action = "type",
                V = type,
                Submit = submit
            });
        }
        if (!string.IsNullOrWhiteSpace(hotkey))
        {
            steps.Add(new InbriskTools.BatchStep
            {
                Action = "hotkey",
                Keys = hotkey
            });
        }

        if (steps.Count == 0)
        {
            return ErrorResult("Malformed", "pass at least one action: app, click, type, or hotkey");
        }

        var batchResult = await Bridge.GhostBatch(
            steps: steps.ToArray(),
            set: null,
            until: null,
            read: null,
            detail: "slim",
            failFast: true,
            fail_fast: true,
            autoDismissModals: "closeOnly",
            auto_dismiss_modals: "closeOnly",
            timeoutMs: 30000,
            timeout_ms: 30000,
            operationId: null,
            operation_id: null,
            ct: ct).ConfigureAwait(false);

        if (batchResult.IsError == true)
        {
            var isNotConnected = false;
            if (batchResult.Content != null)
            {
                foreach (var block in batchResult.Content)
                {
                    if (block is TextContentBlock tb &&
                        (tb.Text.Contains("GhostWorkerNotConnected") || tb.Text.Contains("SessionInactive")))
                    {
                        isNotConnected = true;
                        break;
                    }
                }
            }

            if (isNotConnected)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(app))
                    {
                        await LaunchAsync(app, null, null, null, null, null, null, null, ct).ConfigureAwait(false);
                        if (waitMs > 0) await Task.Delay(waitMs, ct).ConfigureAwait(false);
                    }
                    if (!string.IsNullOrWhiteSpace(click) || target != null)
                    {
                        await ClickAsync(target?.X, target?.Y, "left", false, null, target, ct).ConfigureAwait(false);
                        if (waitMs > 0) await Task.Delay(waitMs, ct).ConfigureAwait(false);
                    }
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        await TypeAsync(type, submit, ct).ConfigureAwait(false);
                    }
                    return new CallToolResult
                    {
                        IsError = false,
                        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { success = true, mode = "local_ghost_desktop", message = "Executed computer_do steps on local Ghost Desktop." }) }]
                    };
                }
                catch (Exception ex)
                {
                    return ErrorResult("LocalGhostExecutionFailed", ex.Message);
                }
            }
        }

        return batchResult;
    }

    /// <summary>
    /// Routes declarative batch execution to the Ghost session coordinator / IPC bus.
    /// </summary>
    public async Task<CallToolResult> BatchAsync(
        InbriskTools.BatchStep[]? steps,
        InbriskTools.FormFieldSpec[]? set,
        InbriskTools.BatchUntilSpec? until,
        InbriskTools.BatchReadSpec[]? read,
        string? detail,
        bool? failFast,
        string? autoDismissModals,
        int? timeoutMs,
        string? operationId,
        CancellationToken ct = default)
    {
        return await Bridge.GhostBatch(
            steps: steps,
            set: set,
            until: until,
            read: read,
            detail: detail,
            failFast: failFast,
            fail_fast: failFast,
            autoDismissModals: autoDismissModals,
            auto_dismiss_modals: autoDismissModals,
            timeoutMs: timeoutMs,
            timeout_ms: timeoutMs,
            operationId: operationId,
            operation_id: operationId,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns windows active on the isolated Ghost Desktop.
    /// </summary>
    public async Task<CallToolResult> WindowsAsync(CancellationToken ct = default)
    {
        return await Bridge.GhostDesktopStatus(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures a screenshot from the isolated Ghost Desktop.
    /// </summary>
    public async Task<CallToolResult> ScreenshotAsync(CancellationToken ct = default)
    {
        return await Bridge.GhostDesktopScreenshot(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Injects a mouse click on the isolated Ghost Desktop.
    /// </summary>
    public async Task<CallToolResult> ClickAsync(
        int? x,
        int? y,
        string? button,
        bool doubleClick,
        string? elementId,
        InbriskTools.TargetSpec? target,
        CancellationToken ct = default)
    {
        int targetX = x ?? target?.X ?? 0;
        int targetY = y ?? target?.Y ?? 0;
        return await Bridge.GhostDesktopClick(targetX, targetY, button ?? "left", doubleClick, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Injects text into the focused window on the isolated Ghost Desktop.
    /// </summary>
    public async Task<CallToolResult> TypeAsync(
        string text,
        bool submit = false,
        CancellationToken ct = default)
    {
        string textToType = submit ? text + "\n" : text;
        return await Bridge.GhostDesktopType(textToType, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inspects UI elements of a window on the Ghost Desktop.
    /// </summary>
    public async Task<CallToolResult> InspectAsync(
        string? hwnd,
        string? elementId,
        string? detail,
        int? maxElements,
        CancellationToken ct = default)
    {
        long targetH = 0;
        if (!string.IsNullOrWhiteSpace(hwnd))
        {
            if (long.TryParse(hwnd.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hwnd[2..] : hwnd,
                    System.Globalization.NumberStyles.HexNumber, null, out var parsedH))
            {
                targetH = parsedH;
            }
            else if (long.TryParse(hwnd, out parsedH))
            {
                targetH = parsedH;
            }
        }

        if (targetH == 0)
        {
            try
            {
                var hDesktop = GhostDesktopNative.OpenDesktop(GhostDesktopNative.DefaultGhostDesktopName, GhostDesktopNative.DESKTOP_ALL_ACCESS, false);
                if (hDesktop != IntPtr.Zero)
                {
                    try
                    {
                        var hwnds = GhostDesktopNative.EnumerateWindows(hDesktop);
                        foreach (var hw in hwnds)
                        {
                            var (title, className, visible) = GhostDesktopNative.GetWindowDetails(hw);
                            if (visible && !string.IsNullOrWhiteSpace(title))
                            {
                                targetH = (long)hw;
                                break;
                            }
                        }
                        if (targetH == 0 && hwnds.Count > 0)
                        {
                            targetH = (long)hwnds[0];
                        }
                    }
                    finally
                    {
                        GhostDesktopNative.CloseDesktop(hDesktop);
                    }
                }
            }
            catch { }
        }

        if (targetH != 0)
        {
            try
            {
                var els = await _session.Rt.InspectAsync(targetH, ct: ct).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine($"[Ghost Desktop Window 0x{targetH:X}] Elements: {els.Count}");
                int cap = maxElements ?? 40;
                foreach (var el in els.Take(cap))
                {
                    sb.AppendLine($"[{el.Id}] {el.Role} \"{el.Name}\" bounds=({el.Bounds.X},{el.Bounds.Y} {el.Bounds.Width}x{el.Bounds.Height})");
                }
                return new CallToolResult
                {
                    IsError = false,
                    Content = [new TextContentBlock { Text = sb.ToString() }]
                };
            }
            catch (Exception ex)
            {
                return new CallToolResult
                {
                    IsError = false,
                    Content = [new TextContentBlock { Text = $"Ghost desktop window 0x{targetH:X} active. Inspection result: {ex.Message}" }]
                };
            }
        }

        return new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock { Text = "Ghost desktop has no active windows. Use computer_launch with ghost:true to launch an application." }]
        };
    }

    /// <summary>
    /// Observes the state of the Ghost Desktop with diagnostics and optional screenshot.
    /// </summary>
    public async Task<CallToolResult> ObserveAsync(
        string? mode,
        string? hwnd,
        string? detail,
        int? maxElements,
        CancellationToken ct = default)
    {
        var statusResult = await Bridge.GhostDesktopStatus(ct).ConfigureAwait(false);
        var blocks = new List<ContentBlock>();
        if (statusResult.Content != null)
        {
            blocks.AddRange(statusResult.Content);
        }

        var policy = (mode ?? "auto").ToLowerInvariant();
        if (policy is not "semantic")
        {
            try
            {
                var screenshotResult = await Bridge.GhostDesktopScreenshot(ct).ConfigureAwait(false);
                if (screenshotResult.Content != null)
                {
                    foreach (var b in screenshotResult.Content)
                    {
                        if (b is ImageContentBlock img)
                        {
                            blocks.Add(img);
                        }
                    }
                }
            }
            catch { }
        }

        return new CallToolResult
        {
            IsError = statusResult.IsError,
            Content = blocks
        };
    }

    private static CallToolResult EmergencyResult() =>
        ErrorResult("EmergencyStopped", "Computer control is stopped by the local emergency hotkey; only the local user can resume.");

    private static CallToolResult ErrorResult(string kind, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new
            {
                success = false,
                error = kind,
                message
            })
        }]
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_bridgeLazy.IsValueCreated)
        {
            try { _bridgeLazy.Value.Dispose(); } catch { }
        }
    }
}
