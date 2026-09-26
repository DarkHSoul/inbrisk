using Inbrisk.Core;

namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// Specialist adapter for Inbrisk.TestApp. Demonstrates hybrid UI + API execution:
/// allows automated tests and agents to query or mutate app state directly
/// without requiring coordinate clicks, and to verify UI mutations against state.
/// </summary>
public sealed class TestAppAdapter : IApplicationAdapter
{
    public string AdapterId => "testapp";
    public string DisplayName => "Inbrisk TestApp Specialist Adapter";

    public IReadOnlyList<string> SupportedActions { get; } = new[]
    {
        "read_state", "write_state", "save", "save_secondary", "load_remote", "toggle_anim"
    };

    public static readonly string StateDir =
        Path.Combine(Path.GetTempPath(), "inbrisk_testapp");
    public static readonly string StateFile =
        Path.Combine(StateDir, "state.txt");

    public bool IsApplicable(string? processName, long? hwnd)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return clean.Equals("Inbrisk.TestApp", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanHandle(string action, TargetRef? target = null, IReadOnlyDictionary<string, object?>? args = null)
    {
        var act = (action ?? "").ToLowerInvariant();
        return act is "read_state" or "write_state" or "save" or "save_secondary" or "load_remote" or "toggle_anim";
    }

    public Task<AdapterResult> ExecuteAsync(
        string action,
        TargetRef? target = null,
        IReadOnlyDictionary<string, object?>? args = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var act = (action ?? "").ToLowerInvariant();

        try
        {
            Directory.CreateDirectory(StateDir);

            switch (act)
            {
                case "read_state":
                {
                    var text = ReadStateTolerant();
                    return Task.FromResult(new AdapterResult(
                        true,
                        "TestApp.ReadState",
                        $"current state: '{text}'",
                        new Dictionary<string, object?> { ["state"] = text }));
                }

                case "write_state":
                {
                    var text = args?.TryGetValue("text", out var t) == true ? t?.ToString() ?? "" : "";
                    WriteStateTolerant(text);
                    return Task.FromResult(new AdapterResult(
                        true,
                        "TestApp.WriteState",
                        $"wrote state: '{text}'",
                        new Dictionary<string, object?> { ["state"] = text }));
                }

                case "save":
                {
                    var text = args?.TryGetValue("text", out var t) == true ? t?.ToString() ?? "" : "";
                    var payload = "saved:" + text;
                    WriteStateTolerant(payload);
                    return Task.FromResult(new AdapterResult(
                        true,
                        "TestApp.Save",
                        $"saved with payload: '{payload}'",
                        new Dictionary<string, object?> { ["saved"] = text }));
                }

                case "save_secondary":
                {
                    var text = args?.TryGetValue("text", out var t) == true ? t?.ToString() ?? "" : "";
                    var payload = "saved_secondary:" + text;
                    WriteStateTolerant(payload);
                    return Task.FromResult(new AdapterResult(
                        true,
                        "TestApp.SaveSecondary",
                        $"saved secondary with payload: '{payload}'",
                        new Dictionary<string, object?> { ["savedSecondary"] = text }));
                }

                case "load_remote":
                {
                    WriteStateTolerant("remote:loaded");
                    return Task.FromResult(new AdapterResult(
                        true,
                        "TestApp.LoadRemote",
                        "triggered remote load",
                        new Dictionary<string, object?> { ["remoteStatus"] = "loaded" }));
                }

                case "toggle_anim":
                {
                    var cur = ReadStateTolerant();
                    var next = cur.Contains("animating") ? "canvas:idle" : "canvas:animating";
                    WriteStateTolerant(next);
                    return Task.FromResult(new AdapterResult(
                        true,
                        "TestApp.ToggleAnim",
                        $"toggled animation to: '{next}'",
                        new Dictionary<string, object?> { ["animState"] = next }));
                }

                default:
                    return Task.FromResult(new AdapterResult(
                        false,
                        "TestApp",
                        $"unknown action: '{act}'",
                        null,
                        ErrorCode.Unsupported));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(new AdapterResult(
                false,
                "TestApp",
                $"failed to execute '{act}': {ex.Message}",
                null,
                ErrorCode.Internal));
        }
    }

    private static string ReadStateTolerant()
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (!File.Exists(StateFile)) return "idle";
                using var fs = new FileStream(StateFile, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                return r.ReadToEnd();
            }
            catch (IOException) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) { Thread.Sleep(25); }
        }
        return "idle";
    }

    private static void WriteStateTolerant(string text)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var fs = new FileStream(StateFile, FileMode.Create,
                    FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var w = new StreamWriter(fs);
                w.Write(text);
                return;
            }
            catch (IOException) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) { Thread.Sleep(25); }
        }
    }
}
