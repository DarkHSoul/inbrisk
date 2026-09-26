using System.ComponentModel;
using System.Runtime.InteropServices;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Input;

public readonly record struct HotkeyChord(uint Modifiers, uint VirtualKey, string Display)
{
    public static HotkeyChord Parse(string text)
    {
        uint modifiers = 0;
        uint? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= 0x0002; break;
                case "alt": modifiers |= 0x0001; break;
                case "shift": modifiers |= 0x0004; break;
                case "win" or "windows": modifiers |= 0x0008; break;
                case "pause" when key == null: key = 0x13; break;
                default:
                    if (key != null || !TryVirtualKey(part, out var vk))
                        throw new ArgumentException($"Invalid hotkey '{text}'");
                    key = vk;
                    break;
            }
        }
        if (modifiers == 0 || key == null)
            throw new ArgumentException($"Hotkey needs modifiers and one key: '{text}'");
        return new HotkeyChord(modifiers, key.Value, text);
    }

    private static bool TryVirtualKey(string part, out uint vk)
    {
        vk = 0;
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
        {
            vk = char.ToUpperInvariant(part[0]);
            return true;
        }
        if (part.Length > 1 && part[0] is 'F' or 'f' &&
            int.TryParse(part[1..], out var n) && n is >= 1 and <= 12)
        {
            vk = (uint)(0x70 + n - 1);
            return true;
        }
        return false;
    }
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Action _onPanic;
    private readonly Action _onResume;
    private readonly Action<string> _onFailure;
    private uint _threadId;
    private int _panicAvailable;
    private int _resumeAvailable;

    public HotkeyChord Panic { get; }
    public HotkeyChord Resume { get; }
    public bool PanicAvailable => Volatile.Read(ref _panicAvailable) != 0;
    public bool ResumeAvailable => Volatile.Read(ref _resumeAvailable) != 0;

    public GlobalHotkeyService(string panic, string resume, Action onPanic,
        Action onResume, Action<string> onFailure)
    {
        Panic = HotkeyChord.Parse(panic);
        Resume = HotkeyChord.Parse(resume);
        if (Panic.Modifiers == Resume.Modifiers && Panic.VirtualKey == Resume.VirtualKey)
            throw new ArgumentException("Panic and resume hotkeys must differ");
        _onPanic = onPanic;
        _onResume = onResume;
        _onFailure = onFailure;
        _thread = new Thread(Loop) { IsBackground = true, Name = "InbriskEmergencyHotkeys" };
    }

    public void Start()
    {
        _thread.Start();
        _ready.Wait();
    }

    private void Loop()
    {
        // Ctrl+Pause produces VK_CANCEL (Break), not VK_PAUSE — register both
        // so the physical chord fires regardless of which VK the OS reports.
        const uint vkPause = 0x13, vkCancel = 0x03;
        var panicAlt = Panic.VirtualKey == vkPause;
        var resumeAlt = Resume.VirtualKey == vkPause;
        var panicOk = false; var resumeOk = false;
        var panicAltOk = false; var resumeAltOk = false;
        _threadId = NativeMethods.GetCurrentThreadId();
        try
        {
            if (NativeMethods.RegisterHotKey(IntPtr.Zero, 1, Panic.Modifiers | 0x4000, Panic.VirtualKey))
                panicOk = true;
            if (panicAlt && NativeMethods.RegisterHotKey(IntPtr.Zero, 3, Panic.Modifiers | 0x4000, vkCancel))
                panicAltOk = true;
            if (panicOk || panicAltOk)
                Volatile.Write(ref _panicAvailable, 1);
            else
                _onFailure($"RegisterHotKey failed for {Panic.Display}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            if (NativeMethods.RegisterHotKey(IntPtr.Zero, 2, Resume.Modifiers | 0x4000, Resume.VirtualKey))
                resumeOk = true;
            if (resumeAlt && NativeMethods.RegisterHotKey(IntPtr.Zero, 4, Resume.Modifiers | 0x4000, vkCancel))
                resumeAltOk = true;
            if (resumeOk || resumeAltOk)
                Volatile.Write(ref _resumeAvailable, 1);
            else
                _onFailure($"RegisterHotKey failed for {Resume.Display}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
        finally { _ready.Set(); }
        while (NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message != WmHotkey) continue;
            try
            {
                var id = message.WParam.ToInt32();
                if (id is 1 or 3) _onPanic();
                else if (id is 2 or 4) _onResume();
            }
            catch (Exception e) { _onFailure($"Emergency hotkey callback failed: {e.Message}"); }
        }
        if (panicOk) NativeMethods.UnregisterHotKey(IntPtr.Zero, 1);
        if (panicAltOk) NativeMethods.UnregisterHotKey(IntPtr.Zero, 3);
        if (resumeOk) NativeMethods.UnregisterHotKey(IntPtr.Zero, 2);
        if (resumeAltOk) NativeMethods.UnregisterHotKey(IntPtr.Zero, 4);
    }

    public void Dispose()
    {
        if (_thread.IsAlive && _threadId != 0)
        {
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join();
        }
        _ready.Dispose();
    }
}
