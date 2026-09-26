using System.Runtime.InteropServices;
using Inbrisk.Core;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Universal Windows Media adapter. Provides sub-millisecond, zero-coordinate
/// media control (play, pause, next, previous, volume) for Spotify, VLC,
/// browsers, and Windows media players without requiring window focus or mouse movement.
/// </summary>
public sealed class WindowsMediaAdapter : IApplicationAdapter
{
    public string AdapterId => "media";
    public string DisplayName => "Windows Media & Spotify Adapter";

    public IReadOnlyList<string> SupportedActions { get; } = new[]
    {
        "play", "pause", "play_pause", "toggle_playback",
        "next", "previous", "stop",
        "volume_up", "volume_down", "mute", "media"
    };

    private static readonly HashSet<string> KnownMediaProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotify", "vlc", "wmplayer", "music.ui", "itunes", "aimp", "foobar2000",
        "chrome", "msedge", "firefox", "brave"
    };

    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaStop = 0xB2;
    private const byte VkMediaPlayPause = 0xB3;

    private const uint KeyEventFKeyUp = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public bool IsApplicable(string? processName, long? hwnd)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true; // generic media control applies globally
        return KnownMediaProcesses.Contains(processName) ||
               processName.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("music", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("media", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanHandle(string action, TargetRef? target = null, IReadOnlyDictionary<string, object?>? args = null)
    {
        var act = (action ?? "").ToLowerInvariant();
        return act is "play" or "pause" or "play_pause" or "toggle_playback"
            or "next" or "previous" or "stop"
            or "volume_up" or "volume_down" or "mute" or "media";
    }

    public Task<AdapterResult> ExecuteAsync(
        string action,
        TargetRef? target = null,
        IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var act = (action ?? "").ToLowerInvariant();

        // If action is generic "media", inspect args["command"] or args["subAction"]
        if (act == "media")
        {
            if (args != null && args.TryGetValue("command", out var cmdObj) && cmdObj is string cmdStr)
                act = cmdStr.ToLowerInvariant();
            else if (args != null && args.TryGetValue("subAction", out var subObj) && subObj is string subStr)
                act = subStr.ToLowerInvariant();
            else
                act = "play_pause";
        }

        byte vk;
        string method;
        string detail;

        switch (act)
        {
            case "play":
            case "pause":
            case "play_pause":
            case "toggle_playback":
                vk = VkMediaPlayPause;
                method = "WindowsMedia.PlayPause";
                detail = "sent media play/pause key event";
                break;

            case "next":
            case "next_track":
                vk = VkMediaNextTrack;
                method = "WindowsMedia.NextTrack";
                detail = "sent media next track key event";
                break;

            case "previous":
            case "previous_track":
            case "prev":
                vk = VkMediaPrevTrack;
                method = "WindowsMedia.PreviousTrack";
                detail = "sent media previous track key event";
                break;

            case "stop":
                vk = VkMediaStop;
                method = "WindowsMedia.Stop";
                detail = "sent media stop key event";
                break;

            case "volume_up":
                vk = VkVolumeUp;
                method = "WindowsMedia.VolumeUp";
                detail = "sent volume up key event";
                break;

            case "volume_down":
                vk = VkVolumeDown;
                method = "WindowsMedia.VolumeDown";
                detail = "sent volume down key event";
                break;

            case "mute":
            case "toggle_mute":
                vk = VkVolumeMute;
                method = "WindowsMedia.Mute";
                detail = "sent volume mute key event";
                break;

            default:
                return Task.FromResult(new AdapterResult(
                    false,
                    "WindowsMedia",
                    $"unsupported media action: '{act}'",
                    null,
                    ErrorCode.Unsupported));
        }

        // Dispatch key down + key up
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KeyEventFKeyUp, UIntPtr.Zero);

        var data = new Dictionary<string, object?>
        {
            ["action"] = act,
            ["vkCode"] = $"0x{vk:X2}",
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        return Task.FromResult(new AdapterResult(true, method, detail, data));
    }
}
