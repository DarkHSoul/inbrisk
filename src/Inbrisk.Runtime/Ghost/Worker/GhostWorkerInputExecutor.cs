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



/// <summary>
