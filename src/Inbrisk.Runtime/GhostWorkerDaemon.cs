using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

#region Configuration & Status Models

/// <summary>
/// Configuration parameters for the Ghost Worker Daemon running inside Session 2 (InbriskAgent).
/// </summary>
public record GhostWorkerDaemonConfig
{
    /// <summary>
    /// Named pipe address to connect to (defaults to inbrisk_ghost_bus).
    /// </summary>
    public string PipeName { get; init; } = GhostIpcDefaults.PipeName;

    /// <summary>
    /// Host name running the named pipe server (defaults to local ".").
    /// </summary>
    public string ServerName { get; init; } = ".";

    /// <summary>
    /// Shared memory buffer map name for cross-session frame streaming.
    /// </summary>
    public string SharedBufferMapName { get; init; } = @"Global\InbriskGhostFrameBuffer";

    /// <summary>
    /// Shared synchronization event name for signaling ready frames.
    /// </summary>
    public string SharedEventName { get; init; } = @"Global\InbriskGhostFrameEvent";

    /// <summary>
    /// Target framerate for DXGI desktop capture loop (30-60 FPS).
    /// </summary>
    public int TargetFps { get; init; } = 60;

    /// <summary>
    /// Whether to automatically begin DXGI screen capture upon starting the daemon.
    /// </summary>
    public bool AutoStartCapture { get; init; } = true;

    /// <summary>
    /// Backoff delay in milliseconds before attempting to reconnect to the IPC pipe.
    /// </summary>
    public int ReconnectDelayMs { get; init; } = 1000;

    /// <summary>
    /// Default capture width in pixels.
    /// </summary>
    public int DefaultWidth { get; init; } = 1920;

    /// <summary>
    /// Default capture height in pixels.
    /// </summary>
    public int DefaultHeight { get; init; } = 1080;
}

/// <summary>
/// Live diagnostic and operational status of the Ghost Worker Daemon.
/// </summary>
public record GhostWorkerStatus(
    bool IsRunning,
    bool IsConnected,
    bool IsCapturing,
    int TargetFps,
    int CurrentFps,
    long TotalFramesCaptured,
    int ReconnectCount,
    int SessionId,
    int ProcessId,
    TimeSpan Uptime,
    string? LastError);

#endregion

#region Ghost Input Executor

/// <summary>
/// High-speed automation input executor for Session 2.
/// Parses batch action payloads and drives native keyboard/mouse events.
/// Uses <see cref="IInputService"/> (SendInputService from Inbrisk.Platform.Windows)
/// when available, with a built-in Win32 fallback for environments where platform assemblies are detached.
/// </summary>
public class GhostInputExecutor
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly IInputService _input;

    public IInputService InputService => _input;

    public GhostInputExecutor(IInputService? inputService = null)
    {
        _input = inputService ?? ResolveInputService();
    }

    /// <summary>
    /// Executes a batch action JSON payload received over the Ghost IPC channel.
    /// Handles JSON arrays, batch objects ({ steps: [...] }), and single-action payloads.
    /// </summary>
    public async Task<GhostBatchResult> ExecuteBatchAsync(string batchJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(batchJson))
        {
            return GhostBatchResult.Failed("Batch payload was null or empty.");
        }

        var sw = Stopwatch.StartNew();
        var stepResults = new List<GhostBatchStepResult>();
        int stepsExecuted = 0;

        try
        {
            using var doc = JsonDocument.Parse(batchJson);
            var root = doc.RootElement;

            // 1. Array of steps: [ { "do": "click", ... }, ... ]
            if (root.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var stepElem in root.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    var stepRes = await ExecuteSingleStepAsync(index++, stepElem, ct).ConfigureAwait(false);
                    stepResults.Add(stepRes);
                    stepsExecuted++;

                    if (!stepRes.Ok && !IsContinueOnError(stepElem))
                    {
                        sw.Stop();
                        return GhostBatchResult.Failed(
                            error: stepRes.Error ?? $"Step {index - 1} failed.",
                            stepsExecuted: stepsExecuted,
                            totalDurationMs: sw.ElapsedMilliseconds,
                            results: stepResults,
                            rawJson: batchJson);
                    }
                }
            }
            // 2. Object with steps array: { "steps": [ ... ] } or { "actions": [ ... ] }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                JsonElement stepsArray;
                if (root.TryGetProperty("steps", out stepsArray) && stepsArray.ValueKind == JsonValueKind.Array ||
                    root.TryGetProperty("actions", out stepsArray) && stepsArray.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var stepElem in stepsArray.EnumerateArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        var stepRes = await ExecuteSingleStepAsync(index++, stepElem, ct).ConfigureAwait(false);
                        stepResults.Add(stepRes);
                        stepsExecuted++;

                        if (!stepRes.Ok && !IsContinueOnError(stepElem))
                        {
                            sw.Stop();
                            return GhostBatchResult.Failed(
                                error: stepRes.Error ?? $"Step {index - 1} failed.",
                                stepsExecuted: stepsExecuted,
                                totalDurationMs: sw.ElapsedMilliseconds,
                                results: stepResults,
                                rawJson: batchJson);
                        }
                    }
                }
                // 3. Single action object: { "do": "click", ... } or { "action": "type", ... }
                else if (root.TryGetProperty("do", out _) || root.TryGetProperty("action", out _))
                {
                    var stepRes = await ExecuteSingleStepAsync(0, root, ct).ConfigureAwait(false);
                    stepResults.Add(stepRes);
                    stepsExecuted++;

                    if (!stepRes.Ok)
                    {
                        sw.Stop();
                        return GhostBatchResult.Failed(
                            error: stepRes.Error ?? "Single action failed.",
                            stepsExecuted: stepsExecuted,
                            totalDurationMs: sw.ElapsedMilliseconds,
                            results: stepResults,
                            rawJson: batchJson);
                    }
                }
                else
                {
                    return GhostBatchResult.Failed("Unrecognized batch JSON format (expected steps array or action object).", rawJson: batchJson);
                }
            }
            else
            {
                return GhostBatchResult.Failed("Expected JSON object or array as batch payload.", rawJson: batchJson);
            }

            sw.Stop();
            return GhostBatchResult.Succeeded(
                stepsExecuted: stepsExecuted,
                totalDurationMs: sw.ElapsedMilliseconds,
                results: stepResults,
                rawJson: batchJson);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            return GhostBatchResult.Failed("Batch execution was cancelled.", stepsExecuted, sw.ElapsedMilliseconds, stepResults, batchJson);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return GhostBatchResult.Failed($"Batch execution failed: {ex.Message}", stepsExecuted, sw.ElapsedMilliseconds, stepResults, batchJson);
        }
    }

    private async Task<GhostBatchStepResult> ExecuteSingleStepAsync(int stepIndex, JsonElement stepElem, CancellationToken ct)
    {
        var stepSw = Stopwatch.StartNew();

        string action = GetStringProp(stepElem, "do") ??
                        GetStringProp(stepElem, "action") ??
                        "click";

        string? target = GetStringProp(stepElem, "t") ??
                         GetStringProp(stepElem, "target");

        string? value = GetStringProp(stepElem, "v") ??
                        GetStringProp(stepElem, "value") ??
                        GetStringProp(stepElem, "text");

        bool submit = GetBoolProp(stepElem, "submit", false);
        int? ms = GetIntProp(stepElem, "ms") ?? GetIntProp(stepElem, "waitFor");
        string? keys = GetStringProp(stepElem, "keys");

        try
        {
            switch (action.ToLowerInvariant())
            {
                case "click":
                case "invoke":
                {
                    ParseCoordinates(target, stepElem, out int x, out int y);
                    string? buttonStr = GetStringProp(stepElem, "button");
                    var button = ParseMouseButton(buttonStr);
                    int count = GetIntProp(stepElem, "count") ?? 1;
                    _input.Click(x, y, button, count);
                    break;
                }

                case "double_click":
                case "doubleclick":
                {
                    ParseCoordinates(target, stepElem, out int x, out int y);
                    _input.Click(x, y, MouseButton.Left, count: 2);
                    break;
                }

                case "right_click":
                case "rightclick":
                {
                    ParseCoordinates(target, stepElem, out int x, out int y);
                    _input.Click(x, y, MouseButton.Right, count: 1);
                    break;
                }

                case "middle_click":
                case "middleclick":
                {
                    ParseCoordinates(target, stepElem, out int x, out int y);
                    _input.Click(x, y, MouseButton.Middle, count: 1);
                    break;
                }

                case "type":
                case "write":
                case "text":
                {
                    string textToType = value ?? target ?? string.Empty;
                    _input.TypeText(textToType);
                    if (submit)
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                        _input.KeyPress(KeyCode.Enter);
                    }
                    break;
                }

                case "key":
                case "press":
                {
                    string keyName = value ?? keys ?? target ?? "Enter";
                    var keyCode = ParseKeyCode(keyName) ?? KeyCode.Enter;
                    _input.KeyPress(keyCode);
                    break;
                }

                case "hotkey":
                {
                    string hotkeyStr = keys ?? value ?? target ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(hotkeyStr))
                    {
                        ExecuteHotkey(hotkeyStr);
                    }
                    break;
                }

                case "wait":
                case "sleep":
                {
                    int delayMs = ms ?? (int.TryParse(value, out int parsedMs) ? parsedMs : 100);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    }
                    break;
                }

                case "scroll":
                {
                    ParseCoordinates(target, stepElem, out int x, out int y);
                    int delta = ms ?? (int.TryParse(value, out int parsedDelta) ? parsedDelta : -120);
                    _input.Scroll(x, y, delta);
                    break;
                }

                case "move":
                case "hover":
                {
                    ParseCoordinates(target, stepElem, out int x, out int y);
                    _input.MoveMouse(x, y);
                    break;
                }

                case "drag":
                {
                    int fromX = GetIntProp(stepElem, "fromX") ?? 0;
                    int fromY = GetIntProp(stepElem, "fromY") ?? 0;
                    int toX = GetIntProp(stepElem, "toX") ?? 0;
                    int toY = GetIntProp(stepElem, "toY") ?? 0;
                    int duration = ms ?? 300;
                    _input.Drag(fromX, fromY, toX, toY, duration, ct);
                    break;
                }

                case "launch":
                {
                    string cmd = value ?? target ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true });
                    }
                    break;
                }

                case "set_value":
                case "set":
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        _input.TypeText(value);
                    }
                    break;
                }

                case "release_all":
                case "reset":
                {
                    _input.ReleaseAll();
                    break;
                }

                default:
                {
                    // Fallback to typing if value present, or click
                    if (!string.IsNullOrEmpty(value))
                    {
                        _input.TypeText(value);
                    }
                    else
                    {
                        ParseCoordinates(target, stepElem, out int x, out int y);
                        _input.Click(x, y);
                    }
                    break;
                }
            }

            stepSw.Stop();
            return new GhostBatchStepResult
            {
                Step = stepIndex,
                Do = action,
                Action = action,
                Target = target,
                TargetFull = target,
                Ok = true,
                Ms = stepSw.ElapsedMilliseconds,
                DurationMs = stepSw.ElapsedMilliseconds,
                Value = value
            };
        }
        catch (Exception ex)
        {
            stepSw.Stop();
            return new GhostBatchStepResult
            {
                Step = stepIndex,
                Do = action,
                Action = action,
                Target = target,
                TargetFull = target,
                Ok = false,
                Ms = stepSw.ElapsedMilliseconds,
                DurationMs = stepSw.ElapsedMilliseconds,
                Error = ex.Message,
                Value = value
            };
        }
    }

    private void ExecuteHotkey(string hotkey)
    {
        var parts = hotkey.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;

        var modifiers = new List<KeyCode>();
        KeyCode? mainKey = null;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].ToLowerInvariant();
            if (i < parts.Length - 1)
            {
                if (part is "ctrl" or "control") modifiers.Add(KeyCode.Ctrl);
                else if (part is "shift") modifiers.Add(KeyCode.Shift);
                else if (part is "alt" or "menu") modifiers.Add(KeyCode.Alt);
                else if (part is "win" or "windows" or "meta" or "cmd") modifiers.Add(KeyCode.Win);
                else
                {
                    var code = ParseKeyCode(part);
                    if (code.HasValue) modifiers.Add(code.Value);
                }
            }
            else
            {
                mainKey = ParseKeyCode(part);
            }
        }

        if (mainKey.HasValue)
        {
            _input.Hotkey(modifiers, mainKey.Value);
        }
    }

    private void ParseCoordinates(string? target, JsonElement stepElem, out int x, out int y)
    {
        // 1. Check direct x, y properties on step object
        if (GetIntProp(stepElem, "x") is { } directX && GetIntProp(stepElem, "y") is { } directY)
        {
            x = directX;
            y = directY;
            return;
        }

        // 2. Check target string as "100,200" or "(100, 200)"
        if (!string.IsNullOrWhiteSpace(target))
        {
            string clean = target.Trim('(', ')', '[', ']', '{', '}', ' ');
            var split = clean.Split(new[] { ',', ';', 'x', 'X' }, StringSplitOptions.TrimEntries);
            if (split.Length >= 2 &&
                int.TryParse(split[0], out int px) &&
                int.TryParse(split[1], out int py))
            {
                x = px;
                y = py;
                return;
            }
        }

        // 3. Fallback to current cursor position
        var cur = _input.CursorPosition();
        x = cur.X;
        y = cur.Y;
    }

    private static MouseButton ParseMouseButton(string? btn) =>
        btn?.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left
        };

    private static KeyCode? ParseKeyCode(string keyStr)
    {
        if (string.IsNullOrWhiteSpace(keyStr)) return null;
        string k = keyStr.Trim().ToLowerInvariant();

        return k switch
        {
            "enter" or "return" => KeyCode.Enter,
            "esc" or "escape" => KeyCode.Escape,
            "tab" => KeyCode.Tab,
            "space" or "spacebar" => KeyCode.Space,
            "backspace" or "back" => KeyCode.Backspace,
            "delete" or "del" => KeyCode.Delete,
            "left" => KeyCode.Left,
            "right" => KeyCode.Right,
            "up" => KeyCode.Up,
            "down" => KeyCode.Down,
            "home" => KeyCode.Home,
            "end" => KeyCode.End,
            "pageup" or "pgup" => KeyCode.PageUp,
            "pagedown" or "pgdn" => KeyCode.PageDown,
            "ctrl" or "control" => KeyCode.Ctrl,
            "shift" => KeyCode.Shift,
            "alt" or "menu" => KeyCode.Alt,
            "win" or "windows" => KeyCode.Win,
            "pause" => KeyCode.Pause,
            "f1" => KeyCode.F1,
            "f2" => KeyCode.F2,
            "f3" => KeyCode.F3,
            "f4" => KeyCode.F4,
            "f5" => KeyCode.F5,
            "f6" => KeyCode.F6,
            "f7" => KeyCode.F7,
            "f8" => KeyCode.F8,
            "f9" => KeyCode.F9,
            "f10" => KeyCode.F10,
            "f11" => KeyCode.F11,
            "f12" => KeyCode.F12,
            "a" => KeyCode.A,
            "b" => KeyCode.B,
            "c" => KeyCode.C,
            "d" => KeyCode.D,
            "e" => KeyCode.E,
            "f" => KeyCode.F,
            "g" => KeyCode.G,
            "h" => KeyCode.H,
            "i" => KeyCode.I,
            "j" => KeyCode.J,
            "k" => KeyCode.K,
            "l" => KeyCode.L,
            "m" => KeyCode.M,
            "n" => KeyCode.N,
            "o" => KeyCode.O,
            "p" => KeyCode.P,
            "q" => KeyCode.Q,
            "r" => KeyCode.R,
            "s" => KeyCode.S,
            "t" => KeyCode.T,
            "u" => KeyCode.U,
            "v" => KeyCode.V,
            "w" => KeyCode.W,
            "x" => KeyCode.X,
            "y" => KeyCode.Y,
            "z" => KeyCode.Z,
            "0" => KeyCode.D0,
            "1" => KeyCode.D1,
            "2" => KeyCode.D2,
            "3" => KeyCode.D3,
            "4" => KeyCode.D4,
            "5" => KeyCode.D5,
            "6" => KeyCode.D6,
            "7" => KeyCode.D7,
            "8" => KeyCode.D8,
            "9" => KeyCode.D9,
            _ => Enum.TryParse<KeyCode>(k, ignoreCase: true, out var code) ? code : null
        };
    }

    private static bool IsContinueOnError(JsonElement elem) =>
        GetBoolProp(elem, "continueOnError", false) || GetBoolProp(elem, "continue_on_error", false);

    private static string? GetStringProp(JsonElement elem, string name)
    {
        if (elem.TryGetProperty(name, out var prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                _ => null
            };
        }
        return null;
    }

    private static int? GetIntProp(JsonElement elem, string name)
    {
        if (elem.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int n))
                return n;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out int parsed))
                return parsed;
        }
        return null;
    }

    private static bool GetBoolProp(JsonElement elem, string name, bool defaultVal)
    {
        if (elem.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultVal;
    }

    private static IInputService ResolveInputService()
    {
        try
        {
            var type = ResolvePlatformType("Input.SendInputService");
            if (type != null && Activator.CreateInstance(type) is IInputService service)
            {
                return service;
            }
        }
        catch { }

        return new Win32InputFallback();
    }

    private static Type? ResolvePlatformType(string relativeName)
    {
        string fullName = $"Inbrisk.Platform.Windows.{relativeName}";
        var type = Type.GetType($"{fullName}, Inbrisk.Platform.Windows");
        if (type != null) return type;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "Inbrisk.Platform.Windows")
            {
                type = asm.GetType(fullName);
                if (type != null) return type;
            }
        }

        try
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "Inbrisk.Platform.Windows.dll");
            if (File.Exists(dllPath))
            {
                var asm = Assembly.LoadFrom(dllPath);
                return asm.GetType(fullName);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Direct Win32 input simulator fallback when Inbrisk.Platform.Windows assembly is absent.
    /// </summary>
    private sealed class Win32InputFallback : IInputService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public void MoveMouse(int x, int y) => SetCursorPos(x, y);

        public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1)
        {
            SetCursorPos(x, y);
            for (int i = 0; i < count; i++)
            {
                if (button == MouseButton.Right)
                {
                    mouse_event(MOUSEEVENTF_RIGHTDOWN, x, y, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_RIGHTUP, x, y, 0, UIntPtr.Zero);
                }
                else if (button == MouseButton.Middle)
                {
                    mouse_event(MOUSEEVENTF_MIDDLEDOWN, x, y, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_MIDDLEUP, x, y, 0, UIntPtr.Zero);
                }
                else
                {
                    mouse_event(MOUSEEVENTF_LEFTDOWN, x, y, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, x, y, 0, UIntPtr.Zero);
                }
                if (count > 1) Thread.Sleep(50);
            }
        }

        public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300, CancellationToken ct = default)
        {
            SetCursorPos(fromX, fromY);
            mouse_event(MOUSEEVENTF_LEFTDOWN, fromX, fromY, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            SetCursorPos(toX, toY);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_LEFTUP, toX, toY, 0, UIntPtr.Zero);
        }

        public void Scroll(int x, int y, int wheelDelta)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_WHEEL, x, y, (uint)wheelDelta, UIntPtr.Zero);
        }

        public void KeyPress(KeyCode key)
        {
            byte vk = MapKeyCodeToVk(key);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public void KeyDown(KeyCode key)
        {
            byte vk = MapKeyCodeToVk(key);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
        }

        public void KeyUp(KeyCode key)
        {
            byte vk = MapKeyCodeToVk(key);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key)
        {
            foreach (var mod in modifiers) KeyDown(mod);
            KeyPress(key);
            for (int i = modifiers.Count - 1; i >= 0; i--) KeyUp(modifiers[i]);
        }

        public void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
            {
                if (c == '\n') KeyPress(KeyCode.Enter);
                else if (c == '\t') KeyPress(KeyCode.Tab);
                else if (c == ' ') KeyPress(KeyCode.Space);
                else if (char.IsLetterOrDigit(c))
                {
                    bool isUpper = char.IsUpper(c);
                    if (isUpper) KeyDown(KeyCode.Shift);
                    var k = ParseKeyCode(c.ToString());
                    if (k.HasValue) KeyPress(k.Value);
                    if (isUpper) KeyUp(KeyCode.Shift);
                }
            }
        }

        public (int X, int Y) CursorPosition()
        {
            GetCursorPos(out var p);
            return (p.X, p.Y);
        }

        public void ReleaseAll()
        {
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
            keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Ctrl
            keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Shift
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Alt
            keybd_event(0x5B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Win
        }

        private static byte MapKeyCodeToVk(KeyCode key) => key switch
        {
            KeyCode.Enter => 0x0D,
            KeyCode.Escape => 0x1B,
            KeyCode.Tab => 0x09,
            KeyCode.Space => 0x20,
            KeyCode.Backspace => 0x08,
            KeyCode.Delete => 0x2E,
            KeyCode.Left => 0x25,
            KeyCode.Up => 0x26,
            KeyCode.Right => 0x27,
            KeyCode.Down => 0x28,
            KeyCode.Home => 0x24,
            KeyCode.End => 0x23,
            KeyCode.PageUp => 0x21,
            KeyCode.PageDown => 0x22,
            KeyCode.Ctrl => 0x11,
            KeyCode.Shift => 0x10,
            KeyCode.Alt => 0x12,
            KeyCode.Win => 0x5B,
            KeyCode.Pause => 0x13,
            KeyCode.F1 => 0x70,
            KeyCode.F2 => 0x71,
            KeyCode.F3 => 0x72,
            KeyCode.F4 => 0x73,
            KeyCode.F5 => 0x74,
            KeyCode.F6 => 0x75,
            KeyCode.F7 => 0x76,
            KeyCode.F8 => 0x77,
            KeyCode.F9 => 0x78,
            KeyCode.F10 => 0x79,
            KeyCode.F11 => 0x7A,
            KeyCode.F12 => 0x7B,
            KeyCode.A => (byte)'A',
            KeyCode.B => (byte)'B',
            KeyCode.C => (byte)'C',
            KeyCode.D => (byte)'D',
            KeyCode.E => (byte)'E',
            KeyCode.F => (byte)'F',
            KeyCode.G => (byte)'G',
            KeyCode.H => (byte)'H',
            KeyCode.I => (byte)'I',
            KeyCode.J => (byte)'J',
            KeyCode.K => (byte)'K',
            KeyCode.L => (byte)'L',
            KeyCode.M => (byte)'M',
            KeyCode.N => (byte)'N',
            KeyCode.O => (byte)'O',
            KeyCode.P => (byte)'P',
            KeyCode.Q => (byte)'Q',
            KeyCode.R => (byte)'R',
            KeyCode.S => (byte)'S',
            KeyCode.T => (byte)'T',
            KeyCode.U => (byte)'U',
            KeyCode.V => (byte)'V',
            KeyCode.W => (byte)'W',
            KeyCode.X => (byte)'X',
            KeyCode.Y => (byte)'Y',
            KeyCode.Z => (byte)'Z',
            KeyCode.D0 => (byte)'0',
            KeyCode.D1 => (byte)'1',
            KeyCode.D2 => (byte)'2',
            KeyCode.D3 => (byte)'3',
            KeyCode.D4 => (byte)'4',
            KeyCode.D5 => (byte)'5',
            KeyCode.D6 => (byte)'6',
            KeyCode.D7 => (byte)'7',
            KeyCode.D8 => (byte)'8',
            KeyCode.D9 => (byte)'9',
            _ => 0
        };
    }
}

#endregion

#region Native Shared Frame Buffer Writer

/// <summary>
/// High-speed shared memory writer writing desktop frames directly into the cross-session
/// Inbrisk double-buffered shared memory layout with NULL DACL permissions.
/// Binary-compatible with <c>GhostSharedFrameBuffer</c> and <c>GhostPipRenderer</c>.
/// </summary>
internal sealed class NativeGhostFrameBufferWriter : IDisposable
{
    private const uint DefaultMagic = 0x49424642; // "IBFB"
    private const int PageAlignment = 4096;
    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
    private const uint SECURITY_DESCRIPTOR_REVISION = 1;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeSecurityDescriptor(IntPtr pSecurityDescriptor, uint dwRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSecurityDescriptorDacl(IntPtr pSecurityDescriptor, [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent, IntPtr pDacl, [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    private readonly string _mapName;
    private readonly string _eventName;
    private readonly long _slotCapacity;
    private readonly long _slot0Offset;
    private readonly long _slot1Offset;

    private IntPtr _hMap = IntPtr.Zero;
    private IntPtr _mappedView = IntPtr.Zero;
    private IntPtr _hEvent = IntPtr.Zero;
    private long _frameIndex;
    private bool _disposed;

    public bool IsInitialized => _mappedView != IntPtr.Zero && !_disposed;

    public NativeGhostFrameBufferWriter(string mapName, string eventName, int maxSlotSize = 3840 * 2160 * 4)
    {
        _mapName = mapName;
        _eventName = eventName;
        _slotCapacity = maxSlotSize;

        // SlotControlHeader size is 64 bytes (Magic, Width, Height, Stride, FrameIndex, TimestampTicks, WriteSequence, DataLength, Reserved1, Reserved2)
        const long slotHeaderSize = 64;
        long alignedSlotSpan = AlignUp(slotHeaderSize + maxSlotSize, PageAlignment);
        _slot0Offset = PageAlignment;
        _slot1Offset = _slot0Offset + alignedSlotSpan;
        long totalMappingSize = _slot1Offset + alignedSlotSpan;

        InitSharedMemory(totalMappingSize);
    }

    private void InitSharedMemory(long totalSize)
    {
        IntPtr pSd = Marshal.AllocHGlobal(64);
        try
        {
            InitializeSecurityDescriptor(pSd, SECURITY_DESCRIPTOR_REVISION);
            SetSecurityDescriptorDacl(pSd, true, IntPtr.Zero, false);

            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = pSd,
                bInheritHandle = false
            };

            uint sizeHigh = (uint)(totalSize >> 32);
            uint sizeLow = (uint)(totalSize & 0xFFFFFFFF);

            _hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, ref sa, PAGE_READWRITE, sizeHigh, sizeLow, _mapName);
            if (_hMap == IntPtr.Zero)
            {
                // Fallback to local namespace if Global requires elevated admin
                string localName = _mapName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)
                    ? @"Local\" + _mapName.Substring(7)
                    : _mapName;
                _hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, ref sa, PAGE_READWRITE, sizeHigh, sizeLow, localName);
            }

            if (_hMap != IntPtr.Zero)
            {
                _mappedView = MapViewOfFile(_hMap, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
                if (_mappedView != IntPtr.Zero)
                {
                    Marshal.WriteInt32(_mappedView, 0, unchecked((int)DefaultMagic));
                    Marshal.WriteInt32(_mappedView, 4, 1); // Version
                    Marshal.WriteInt32(_mappedView, 8, 2); // SlotCount
                    Marshal.WriteInt32(_mappedView, 12, -1); // ActiveSlotIndex
                    Marshal.WriteInt64(_mappedView, 16, _slotCapacity);
                    Marshal.WriteInt64(_mappedView, 24, _slot0Offset);
                    Marshal.WriteInt64(_mappedView, 32, _slot1Offset);
                    Marshal.WriteInt64(_mappedView, 40, 0); // TotalFramesWritten
                }
            }

            // Frame ready event
            _hEvent = CreateEventW(ref sa, false, false, _eventName);
            if (_hEvent == IntPtr.Zero && _eventName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase))
            {
                string localEvt = @"Local\" + _eventName.Substring(7);
                _hEvent = CreateEventW(ref sa, false, false, localEvt);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pSd);
        }
    }

    public void WriteFrame(int width, int height, int stride, byte[] pixelData)
    {
        if (_disposed || _mappedView == IntPtr.Zero || pixelData == null || pixelData.Length == 0) return;
        if (pixelData.Length > _slotCapacity) return;

        // Double buffer slot selection: inactive slot
        int currentActive = Marshal.ReadInt32(_mappedView, 12);
        int targetSlot = (currentActive == 0) ? 1 : 0;

        long slotOffset = (targetSlot == 0) ? _slot0Offset : _slot1Offset;
        IntPtr slotBase = IntPtr.Add(_mappedView, (int)slotOffset);

        // Seqlock write start: sequence increment to odd
        long seq = Marshal.ReadInt64(slotBase, 32);
        Marshal.WriteInt64(slotBase, 32, seq + 1);
        Thread.MemoryBarrier();

        // Memory copy to slot pixels (offset 64 bytes after SlotControlHeader)
        IntPtr destPixels = IntPtr.Add(slotBase, 64);
        Marshal.Copy(pixelData, 0, destPixels, pixelData.Length);

        // Populate slot header
        Marshal.WriteInt32(slotBase, 0, unchecked((int)DefaultMagic));
        Marshal.WriteInt32(slotBase, 4, width);
        Marshal.WriteInt32(slotBase, 8, height);
        Marshal.WriteInt32(slotBase, 12, stride);
        Marshal.WriteInt64(slotBase, 16, Interlocked.Increment(ref _frameIndex));
        Marshal.WriteInt64(slotBase, 24, DateTime.UtcNow.Ticks);
        Marshal.WriteInt64(slotBase, 40, pixelData.Length);

        // Seqlock write complete: sequence increment to even
        Thread.MemoryBarrier();
        Marshal.WriteInt64(slotBase, 32, seq + 2);
        Thread.MemoryBarrier();

        // Commit active slot
        Marshal.WriteInt32(_mappedView, 12, targetSlot);
        long total = Marshal.ReadInt64(_mappedView, 40);
        Marshal.WriteInt64(_mappedView, 40, total + 1);

        // Signal consumer event
        if (_hEvent != IntPtr.Zero)
        {
            SetEvent(_hEvent);
        }
    }

    private static long AlignUp(long val, int align) => (val + (align - 1)) & ~(align - 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mappedView != IntPtr.Zero)
        {
            UnmapViewOfFile(_mappedView);
            _mappedView = IntPtr.Zero;
        }

        if (_hMap != IntPtr.Zero)
        {
            CloseHandle(_hMap);
            _hMap = IntPtr.Zero;
        }

        if (_hEvent != IntPtr.Zero)
        {
            CloseHandle(_hEvent);
            _hEvent = IntPtr.Zero;
        }
    }
}

#endregion

#region DXGI Screen Capture Engine & GDI Fallback

internal interface IGhostCaptureEngine : IDisposable
{
    bool IsInitialized { get; }
    int Width { get; }
    int Height { get; }
    bool AcquireNextFrame(int timeoutMs, out byte[]? buffer, out int width, out int height, out int stride);
    void ReleaseFrame();
}

/// <summary>
/// Dynamically resolves and drives <c>GhostDxgiCapture</c> from Inbrisk.Platform.Windows.
/// </summary>
internal sealed class ReflectionDxgiCaptureEngine : IGhostCaptureEngine
{
    private readonly object _captureInstance;
    private readonly MethodInfo _acquireMethod;
    private readonly MethodInfo _releaseMethod;
    private readonly PropertyInfo _isInitProp;
    private readonly PropertyInfo _widthProp;
    private readonly PropertyInfo _heightProp;

    public bool IsInitialized => (bool)(_isInitProp.GetValue(_captureInstance) ?? false);
    public int Width => (int)(_widthProp.GetValue(_captureInstance) ?? 1920);
    public int Height => (int)(_heightProp.GetValue(_captureInstance) ?? 1080);

    private ReflectionDxgiCaptureEngine(object instance, Type type)
    {
        _captureInstance = instance;
        _acquireMethod = type.GetMethod("AcquireNextFrame", new[]
        {
            typeof(int),
            typeof(byte[]).MakeByRefType(),
            typeof(int).MakeByRefType(),
            typeof(int).MakeByRefType(),
            typeof(int).MakeByRefType()
        }) ?? throw new MissingMethodException(type.FullName, "AcquireNextFrame(int, out byte[], out int, out int, out int)");

        _releaseMethod = type.GetMethod("ReleaseFrame", Type.EmptyTypes) ??
                         throw new MissingMethodException(type.FullName, "ReleaseFrame()");

        _isInitProp = type.GetProperty("IsInitialized") ??
                      throw new MissingMemberException(type.FullName, "IsInitialized");
        _widthProp = type.GetProperty("Width") ??
                     throw new MissingMemberException(type.FullName, "Width");
        _heightProp = type.GetProperty("Height") ??
                      throw new MissingMemberException(type.FullName, "Height");
    }

    public static ReflectionDxgiCaptureEngine? TryCreate(int adapterIndex = 0, int outputIndex = 0)
    {
        try
        {
            var type = ResolvePlatformType("GhostDxgiCapture");
            if (type == null) return null;

            var ctor = type.GetConstructor(new[] { typeof(int), typeof(int), typeof(bool) });
            if (ctor == null) return null;

            var instance = ctor.Invoke(new object[] { adapterIndex, outputIndex, true });
            var engine = new ReflectionDxgiCaptureEngine(instance, type);
            if (engine.IsInitialized)
            {
                return engine;
            }
            engine.Dispose();
        }
        catch { }

        return null;
    }

    public bool AcquireNextFrame(int timeoutMs, out byte[]? buffer, out int width, out int height, out int stride)
    {
        object?[] args = new object?[] { timeoutMs, null, 0, 0, 0 };
        bool ok = (bool)(_acquireMethod.Invoke(_captureInstance, args) ?? false);
        if (ok)
        {
            buffer = (byte[]?)args[1];
            width = (int)(args[2] ?? 0);
            height = (int)(args[3] ?? 0);
            stride = (int)(args[4] ?? 0);
            return true;
        }

        buffer = null;
        width = 0;
        height = 0;
        stride = 0;
        return false;
    }

    public void ReleaseFrame()
    {
        try
        {
            _releaseMethod.Invoke(_captureInstance, null);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_captureInstance is IDisposable d)
        {
            d.Dispose();
        }
    }

    private static Type? ResolvePlatformType(string relativeName)
    {
        string fullName = $"Inbrisk.Platform.Windows.{relativeName}";
        var type = Type.GetType($"{fullName}, Inbrisk.Platform.Windows");
        if (type != null) return type;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "Inbrisk.Platform.Windows")
            {
                type = asm.GetType(fullName);
                if (type != null) return type;
            }
        }

        try
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "Inbrisk.Platform.Windows.dll");
            if (File.Exists(dllPath))
            {
                var asm = Assembly.LoadFrom(dllPath);
                return asm.GetType(fullName);
            }
        }
        catch { }

        return null;
    }
}

/// <summary>
/// Native GDI desktop capture fallback when DXGI desktop duplication is unavailable
/// (e.g. headless loopback session initial bootstrap, locked desktop, or detached GPU driver).
/// </summary>
internal sealed class GdiDesktopCaptureEngine : IGhostCaptureEngine
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;

    private int _width;
    private int _height;
    private bool _disposed;

    public bool IsInitialized => !_disposed && _width > 0 && _height > 0;
    public int Width => _width;
    public int Height => _height;

    public GdiDesktopCaptureEngine(int defaultWidth = 1920, int defaultHeight = 1080)
    {
        try
        {
            _width = GetSystemMetrics(SM_CXSCREEN);
            _height = GetSystemMetrics(SM_CYSCREEN);
        }
        catch { }

        if (_width <= 0) _width = defaultWidth;
        if (_height <= 0) _height = defaultHeight;
    }

    public bool AcquireNextFrame(int timeoutMs, out byte[]? buffer, out int width, out int height, out int stride)
    {
        width = _width;
        height = _height;
        stride = width * 4;
        buffer = null;

        if (_disposed) return false;

        IntPtr hDesktop = GetDesktopWindow();
        IntPtr hdcScreen = GetDC(hDesktop);
        if (hdcScreen == IntPtr.Zero) return false;

        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
        IntPtr hOld = SelectObject(hdcMem, hBitmap);

        try
        {
            BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, SRCCOPY);

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // Top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            buffer = new byte[height * stride];
            GetDIBits(hdcMem, hBitmap, 0, (uint)height, buffer, ref bmi, DIB_RGB_COLORS);
            return true;
        }
        finally
        {
            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(hDesktop, hdcScreen);
        }
    }

    public void ReleaseFrame() { }

    public void Dispose()
    {
        _disposed = true;
    }
}

#endregion

#region Ghost Worker Daemon

/// <summary>
/// Session 2 Ghost Worker Service (running inside InbriskAgent desktop session).
/// Connects to Session 1 via Named Pipe (<c>\\.\pipe\inbrisk_ghost_bus</c>) with auto-reconnect,
/// listens for actions (execute_batch, start_capture, ping, terminate),
/// and runs an independent background DXGI desktop capture loop (30-60 FPS) feeding
/// fresh frames into the cross-session shared memory buffer (<c>GhostSharedFrameBuffer</c>).
/// </summary>
public sealed class GhostWorkerDaemon : IAsyncDisposable, IDisposable
{
    private readonly GhostWorkerDaemonConfig _config;
    private readonly GhostInputExecutor _inputExecutor;
    private readonly GhostIpcClient _ipcClient;
    private readonly object _syncRoot = new();

    private CancellationTokenSource? _cts;
    private Task? _connectLoopTask;

    // Capture Loop State
    private Thread? _captureThread;
    private volatile bool _isCapturing;
    private IGhostCaptureEngine? _captureEngine;
    private NativeGhostFrameBufferWriter? _frameWriter;

    // Diagnostic & Telemetry Counters
    private readonly Stopwatch _uptimeWatch = Stopwatch.StartNew();
    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    private long _totalFramesCaptured;
    private int _fpsCounter;
    private double _currentFps;
    private int _reconnectCount;
    private string? _lastError;
    private bool _isTerminated;
    private int _disposed;

    #region Properties

    public GhostWorkerDaemonConfig Config => _config;

    public GhostInputExecutor InputExecutor => _inputExecutor;

    public bool IsRunning => _connectLoopTask != null && !_isTerminated && _disposed == 0;

    public bool IsConnected => _ipcClient.IsConnected;

    public bool IsCapturing => _isCapturing;

    public int CurrentFps => (int)Math.Round(Volatile.Read(ref _currentFps));

    public long TotalFramesCaptured => Volatile.Read(ref _totalFramesCaptured);

    public int ReconnectCount => Volatile.Read(ref _reconnectCount);

    public string? LastError => _lastError;

    #endregion

    #region Constructors

    public GhostWorkerDaemon(GhostWorkerDaemonConfig? config = null, GhostInputExecutor? inputExecutor = null)
    {
        _config = config ?? new GhostWorkerDaemonConfig();
        _inputExecutor = inputExecutor ?? new GhostInputExecutor();
        _ipcClient = new GhostIpcClient(_config.PipeName, _config.ServerName);

        RegisterIpcHandlers();
    }

    #endregion

    #region Lifecycle & Public Control

    /// <summary>
    /// Starts the Ghost Worker Daemon background connection and capture tasks.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        lock (_syncRoot)
        {
            if (_connectLoopTask != null) return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _connectLoopTask = RunConnectLoopAsync(_cts.Token);

            if (_config.AutoStartCapture)
            {
                StartCaptureInternal(_config.TargetFps);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Runs the daemon continuously until the provided token is cancelled or a terminate action is received.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        if (_connectLoopTask != null)
        {
            try
            {
                await _connectLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Gracefully stops the worker daemon, capture loop, and IPC connection.
    /// </summary>
    public async Task StopAsync()
    {
        lock (_syncRoot)
        {
            _isTerminated = true;
            _cts?.Cancel();
            StopCaptureInternal();
        }

        if (_connectLoopTask != null)
        {
            try { await _connectLoopTask.ConfigureAwait(false); } catch { }
            _connectLoopTask = null;
        }

        await _ipcClient.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the background DXGI capture loop feeding frames to the shared memory buffer.
    /// </summary>
    public bool StartCapture(int? fps = null)
    {
        lock (_syncRoot)
        {
            return StartCaptureInternal(fps ?? _config.TargetFps);
        }
    }

    /// <summary>
    /// Stops the background DXGI capture loop.
    /// </summary>
    public void StopCapture()
    {
        lock (_syncRoot)
        {
            StopCaptureInternal();
        }
    }

    /// <summary>
    /// Returns current runtime snapshot for diagnostics and RPC status calls.
    /// </summary>
    public GhostWorkerStatus GetStatus()
    {
        int sessionId = 2;
        try { sessionId = Process.GetCurrentProcess().SessionId; } catch { }

        return new GhostWorkerStatus(
            IsRunning: IsRunning,
            IsConnected: IsConnected,
            IsCapturing: IsCapturing,
            TargetFps: _config.TargetFps,
            CurrentFps: CurrentFps,
            TotalFramesCaptured: TotalFramesCaptured,
            ReconnectCount: ReconnectCount,
            SessionId: sessionId,
            ProcessId: Environment.ProcessId,
            Uptime: _uptimeWatch.Elapsed,
            LastError: _lastError);
    }

    #endregion

    #region IPC Message Dispatching & Handlers

    private void RegisterIpcHandlers()
    {
        // 1. "ping" -> "pong"
        _ipcClient.RegisterHandler("ping", _ => Task.FromResult("pong"));

        // 2. "terminate" -> Clean graceful daemon shutdown
        _ipcClient.RegisterHandler("terminate", _ =>
        {
            Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                await StopAsync().ConfigureAwait(false);
            });
            return Task.FromResult(JsonSerializer.Serialize(new { status = "terminating", message = "Ghost worker daemon shutting down cleanly." }));
        });

        // 3. "start_capture" -> Starts DXGI capture loop
        _ipcClient.RegisterHandler("start_capture", payloadJson =>
        {
            int fps = _config.TargetFps;

            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("fps", out var fpsProp) && fpsProp.TryGetInt32(out int parsedFps))
                    {
                        fps = parsedFps;
                    }
                }
                catch { }
            }

            bool ok = StartCapture(fps);
            var response = JsonSerializer.Serialize(new
            {
                status = ok ? "started" : "failed",
                isCapturing = IsCapturing,
                fps = CurrentFps,
                targetFps = _config.TargetFps,
                error = _lastError
            });
            return Task.FromResult(response);
        });

        // 4. "stop_capture" -> Stops DXGI capture loop
        _ipcClient.RegisterHandler("stop_capture", _ =>
        {
            StopCapture();
            return Task.FromResult(JsonSerializer.Serialize(new { status = "stopped", isCapturing = IsCapturing }));
        });

        // 5. "execute_batch" -> GhostInputExecutor executes batch steps
        _ipcClient.RegisterHandler("execute_batch", async payloadJson =>
        {
            var result = await _inputExecutor.ExecuteBatchAsync(payloadJson, _cts?.Token ?? default).ConfigureAwait(false);
            return JsonSerializer.Serialize(result);
        });

        // 6. "status" -> Live diagnostic summary
        _ipcClient.RegisterHandler("status", _ => Task.FromResult(JsonSerializer.Serialize(GetStatus())));
    }

    #endregion

    #region Named Pipe Reconnection Loop

    private async Task RunConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isTerminated)
        {
            try
            {
                if (!_ipcClient.IsConnected)
                {
                    // Attempt connection to host pipe
                    await _ipcClient.ConnectAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }

                // Connected: monitor connection health
                while (_ipcClient.IsConnected && !ct.IsCancellationRequested && !_isTerminated)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _reconnectCount);
                _lastError = $"IPC connection error: {ex.Message}";

                try
                {
                    await Task.Delay(_config.ReconnectDelayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    #endregion

    #region DXGI Desktop Capture Loop Implementation

    private bool StartCaptureInternal(int targetFps)
    {
        if (_isCapturing) return true;

        try
        {
            // 1. Initialize capture engine (Prefer DXGI Desktop Duplication, fallback to GDI)
            _captureEngine?.Dispose();
            _captureEngine = ReflectionDxgiCaptureEngine.TryCreate(0, 0);
            if (_captureEngine == null || !_captureEngine.IsInitialized)
            {
                _captureEngine = new GdiDesktopCaptureEngine(_config.DefaultWidth, _config.DefaultHeight);
            }

            // 2. Initialize shared memory writer
            _frameWriter?.Dispose();
            _frameWriter = new NativeGhostFrameBufferWriter(
                _config.SharedBufferMapName,
                _config.SharedEventName);

            if (!_frameWriter.IsInitialized)
            {
                _lastError = "Failed to initialize shared memory frame buffer writer.";
                return false;
            }

            // 3. Start capture thread
            _isCapturing = true;
            _captureThread = new Thread(CaptureLoopWorker)
            {
                Name = "InbriskGhostWorkerCapture",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"StartCapture error: {ex.Message}";
            StopCaptureInternal();
            return false;
        }
    }

    private void StopCaptureInternal()
    {
        _isCapturing = false;

        if (_captureThread != null && _captureThread.IsAlive)
        {
            if (!_captureThread.Join(1500))
            {
                try { _captureThread.Interrupt(); } catch { }
            }
            _captureThread = null;
        }

        _captureEngine?.Dispose();
        _captureEngine = null;

        _frameWriter?.Dispose();
        _frameWriter = null;
    }

    private void CaptureLoopWorker()
    {
        var frameIntervalStopwatch = Stopwatch.StartNew();
        _fpsWatch.Restart();
        _fpsCounter = 0;

        while (_isCapturing && !_isTerminated)
        {
            try
            {
                int frameBudgetMs = Math.Max(1, 1000 / Math.Clamp(_config.TargetFps, 10, 120));

                if (_captureEngine != null && _frameWriter != null)
                {
                    // Acquire frame from GPU / desktop duplication
                    bool acquired = _captureEngine.AcquireNextFrame(
                        timeoutMs: frameBudgetMs,
                        out byte[]? buffer,
                        out int width,
                        out int height,
                        out int stride);

                    if (acquired && buffer != null && width > 0 && height > 0)
                    {
                        try
                        {
                            // Commit to cross-session shared memory ring buffer
                            _frameWriter.WriteFrame(width, height, stride, buffer);

                            Interlocked.Increment(ref _totalFramesCaptured);
                            _fpsCounter++;
                        }
                        finally
                        {
                            _captureEngine.ReleaseFrame();
                        }
                    }

                    // Rolling FPS calculation
                    if (_fpsWatch.ElapsedMilliseconds >= 1000)
                    {
                        double elapsedSec = _fpsWatch.Elapsed.TotalSeconds;
                        Volatile.Write(ref _currentFps, _fpsCounter / elapsedSec);
                        _fpsCounter = 0;
                        _fpsWatch.Restart();
                    }
                }

                // Pacing throttle for target FPS budget
                long elapsed = frameIntervalStopwatch.ElapsedMilliseconds;
                if (elapsed < frameBudgetMs)
                {
                    int sleepMs = (int)(frameBudgetMs - elapsed);
                    if (sleepMs > 1)
                    {
                        Thread.Sleep(sleepMs);
                    }
                }
                frameIntervalStopwatch.Restart();
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = $"Capture frame error: {ex.Message}";
                Thread.Sleep(30);
            }
        }
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        StopCaptureInternal();

        _cts?.Cancel();
        _cts?.Dispose();

        _ipcClient.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        StopCaptureInternal();

        _cts?.Cancel();
        _cts?.Dispose();

        await _ipcClient.DisposeAsync().ConfigureAwait(false);
    }

    #endregion
}

#endregion
