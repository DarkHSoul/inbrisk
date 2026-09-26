using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Input;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Real global-hotkey emergency stop: RegisterHotKey on a dedicated message
/// thread, triggered by actual SendInput chords. The panic/resume chords in
/// tests are custom (F10-based) so they never collide with the product
/// defaults or other tests.
/// </summary>
[Collection("desktop")]
public sealed class EmergencyHotkeyTests
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;
    private const int VK_A = 0x41; // held test key: not a modifier, won't change the injected chord

    private readonly DesktopFixture _fx;
    public EmergencyHotkeyTests(DesktopFixture fx) => _fx = fx;

    private const string PanicChord = "Ctrl+Alt+F10";
    private const string ResumeChord = "Ctrl+Alt+Shift+F10";

    private static void Inject(SendInputService input, string chord)
    {
        var mods = new List<KeyCode>();
        KeyCode key = KeyCode.F10;
        foreach (var p in chord.Split('+'))
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": mods.Add(KeyCode.Ctrl); break;
                case "alt": mods.Add(KeyCode.Alt); break;
                case "shift": mods.Add(KeyCode.Shift); break;
                case "f10": key = KeyCode.F10; break;
                case "pause": key = KeyCode.Pause; break;
            }
        }
        input.Hotkey(mods, key);
    }

    [Fact]
    public void Panic_ReleasesInput_Resume_IsLocalOnly()
    {
        var marker = Path.Combine(Path.GetTempPath(),
            $"inbrisk-emg-{Guid.NewGuid():N}.flag");
        var prev = Environment.GetEnvironmentVariable("INBRISK_EMERGENCY_STATE");
        Environment.SetEnvironmentVariable("INBRISK_EMERGENCY_STATE", marker);
        try
        {
            var input = new SendInputService();
            using var control = new EmergencyControl();
            control.StartHotkeys(PanicChord, ResumeChord);
            Assert.True(control.PanicAvailable);
            Assert.Equal(ComputerControlState.Active, control.State);

            using var reg = control.RegisterInput(input);
            try { input.ReleaseAll(); } catch { } // clear residue from earlier runs
            input.KeyDown(KeyCode.A);
            input.MouseDown();
            try
            {
            Assert.True((GetAsyncKeyState(VK_A) & 0x8000) != 0);
            Assert.True((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0);

            var sw = Stopwatch.StartNew();
            Inject(input, PanicChord);
            Assert.True(SpinWait.SpinUntil(() =>
                control.State == ComputerControlState.EmergencyStopped, 3000));
            var latency = sw.ElapsedMilliseconds;

            // ApplyStop flips state before the marker write finishes —
            // spin on the marker rather than racing the file write
            Assert.True(SpinWait.SpinUntil(() => File.Exists(marker), 2000),
                "stop marker not persisted");
            // state flips before ReleaseAll runs on the hotkey thread —
            // wait for cleanup rather than asserting a single instant
            Assert.True(SpinWait.SpinUntil(() =>
                input.HeldKeys.Count == 0 &&
                (GetAsyncKeyState(VK_A) & 0x8000) == 0 &&
                (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0, 2000),
                "held key/mouse not released after panic");
            Assert.Null(control.ActionToken());
            control.Dispose(); // shutdown leaves the stop marker in place

            // a fresh process restores the stop from the marker — reconnects
            // can never bypass the panic state
            using var control2 = new EmergencyControl();
            control2.StartHotkeys(PanicChord, ResumeChord);
            Assert.Equal(ComputerControlState.EmergencyStopped, control2.State);
            Assert.Null(control2.ActionToken());

            // the resume chord arrives from an UNBLOCKED injector — it stands
            // in for the user's physical keyboard, which panic never blocks
            // (the panicked `input` service stays blocked until a registered
            // control clears it)
            var unblocked = new SendInputService();
            Inject(unblocked, ResumeChord);
            Assert.True(SpinWait.SpinUntil(() =>
                control2.State == ComputerControlState.Active, 3000));
            Assert.NotNull(control2.ActionToken());
            Assert.False(File.Exists(marker));

            Assert.True(latency < 2000, $"panic latency {latency}ms");
            }
            finally { try { input.ReleaseAll(); } catch { } }
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_EMERGENCY_STATE", prev);
            try { File.Delete(marker); } catch { }
        }
    }

    [Fact]
    public async Task Mcp_Panic_CancelsWait_AndPersistsAcrossReconnect()
    {
        var marker = Path.Combine(Path.GetTempPath(),
            $"inbrisk-emg-mcp-{Guid.NewGuid():N}.flag");
        try { File.Delete(marker); } catch { }
        var env = new Dictionary<string, string?>
        {
            ["INBRISK_PANIC_HOTKEY"] = PanicChord,
            ["INBRISK_RESUME_HOTKEY"] = ResumeChord,
            ["INBRISK_EMERGENCY_STATE"] = marker,
        };
        var injector = new SendInputService();
        try
        {
            await using var client = await Connect(env);
            var call = client.CallToolAsync("computer_wait",
                new Dictionary<string, object?> { ["ms"] = 30000 });
            await Task.Delay(500);
            var sw = Stopwatch.StartNew();
            Inject(injector, PanicChord);
            var stopped = await call;
            Assert.True(stopped.IsError);
            Assert.Contains("EmergencyStopped", TextOf(stopped));
            Assert.True(sw.ElapsedMilliseconds < 5000,
                $"wait did not cancel quickly ({sw.ElapsedMilliseconds}ms)");

            var again = await client.CallToolAsync("computer_wait",
                new Dictionary<string, object?> { ["ms"] = 10 });
            Assert.True(again.IsError);
            Assert.Contains("EmergencyStopped", TextOf(again));

            // MCP must not be able to send the resume chord itself
            var sneak = await client.CallToolAsync("computer_hotkey",
                new Dictionary<string, object?>
                { ["key"] = "F10", ["modifiers"] = new[] { "ctrl", "alt", "shift" } });
            Assert.True(sneak.IsError);
            Assert.Contains("PolicyDenied", TextOf(sneak));
            var tools = await client.ListToolsAsync();
            Assert.DoesNotContain(tools, t =>
                t.Name.Contains("resume", StringComparison.OrdinalIgnoreCase));

            // a NEW server process (fresh session) still sees the stop
            await client.DisposeAsync();
            await using var client2 = await Connect(env);
            var blocked = await client2.CallToolAsync("computer_wait",
                new Dictionary<string, object?> { ["ms"] = 10 });
            Assert.True(blocked.IsError);
            Assert.Contains("EmergencyStopped", TextOf(blocked));

            // human resume → same session works again
            Inject(injector, ResumeChord);
            var ok = await client2.CallToolAsync("computer_wait",
                new Dictionary<string, object?> { ["ms"] = 10 });
            Assert.True(ok.IsError != true, TextOf(ok));
        }
        finally { try { File.Delete(marker); } catch { } }
    }

    [Fact]
    public async Task HotkeyRegistrationFailure_IsAdvertised_AndBlocksControl()
    {
        // occupy the chord in this process so the child's RegisterHotKey
        // genuinely fails — no mocking of the Windows hotkey path
        using var holder = new GlobalHotkeyService("Ctrl+Alt+F11",
            "Ctrl+Alt+Shift+F11", () => { }, () => { }, _ => { });
        holder.Start();
        Assert.True(holder.PanicAvailable);

        var env = new Dictionary<string, string?>
        {
            ["INBRISK_PANIC_HOTKEY"] = "Ctrl+Alt+F11",
            ["INBRISK_RESUME_HOTKEY"] = "Ctrl+Alt+Shift+F11",
            ["INBRISK_EMERGENCY_STATE"] = Path.Combine(Path.GetTempPath(),
                $"inbrisk-emg-fail-{Guid.NewGuid():N}.flag"),
        };
        await using var client = await Connect(env);

        // capabilities must advertise the failure, not silently continue
        var cap = await client.ReadResourceAsync("inbrisk://capabilities");
        var text = string.Join("\n",
            cap.Contents.OfType<TextResourceContents>().Select(t => t.Text));
        Assert.Contains("\"emergencyHotkeyAvailable\":false", text);
        Assert.Contains("EmergencyStopped", text);

        // with no working panic key, computer control must not run
        var blocked = await client.CallToolAsync("computer_wait",
            new Dictionary<string, object?> { ["ms"] = 10 });
        Assert.True(blocked.IsError);
        Assert.Contains("EmergencyStopped", TextOf(blocked));
    }

    /// <summary>Panic mid-run: a computer_run blocked inside wait_for must
    /// abort, deny subsequent control, and leave zero owned input.</summary>
    [Fact]
    public async Task Mcp_Panic_DuringRun_Cancels_AndLeavesInputClean()
    {
        var marker = Path.Combine(Path.GetTempPath(),
            $"inbrisk-emg-run-{Guid.NewGuid():N}.flag");
        try { File.Delete(marker); } catch { }
        var env = new Dictionary<string, string?>
        {
            ["INBRISK_PANIC_HOTKEY"] = PanicChord,
            ["INBRISK_RESUME_HOTKEY"] = ResumeChord,
            ["INBRISK_EMERGENCY_STATE"] = marker,
        };
        var injector = new SendInputService();
        try
        {
            await using var client = await Connect(env);
            var steps = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["action"] = "wait_for",
                    ["query"] = "element-that-never-exists-xq9",
                    ["ms"] = 30000,
                },
            };
            var call = client.CallToolAsync("computer_run",
                new Dictionary<string, object?> { ["steps"] = steps });
            await Task.Delay(700);
            Inject(injector, PanicChord);
            var res = await call;
            Assert.True(res.IsError, TextOf(res));
            var resText = TextOf(res);
            Assert.True(resText.Contains("EmergencyStopped"),
                "expected EmergencyStopped, got: " + resText);

            var denied = await client.CallToolAsync("computer_click",
                new Dictionary<string, object?>
                { ["target"] = new Dictionary<string, object?> { ["hwnd"] = $"0x{_fx.Hwnd:X}" } });
            Assert.True(denied.IsError);
            Assert.Contains("EmergencyStopped", TextOf(denied));

            await AssertServerInputClean(client);
            Inject(injector, ResumeChord);
            Assert.True(SpinWait.SpinUntil(() =>
                !File.Exists(marker), 3000), "resume chord did not clear stop");
        }
        finally
        {
            try { Inject(injector, ResumeChord); } catch { }
            try { File.Delete(marker); } catch { }
        }
    }

    /// <summary>Panic mid-type: injected characters stop, held keys unwind,
    /// and the post-panic diagnostic reports zero owned input.</summary>
    [Fact]
    public async Task Mcp_Panic_DuringType_ReleasesHeld()
    {
        var marker = Path.Combine(Path.GetTempPath(),
            $"inbrisk-emg-type-{Guid.NewGuid():N}.flag");
        try { File.Delete(marker); } catch { }
        var env = new Dictionary<string, string?>
        {
            ["INBRISK_PANIC_HOTKEY"] = PanicChord,
            ["INBRISK_RESUME_HOTKEY"] = ResumeChord,
            ["INBRISK_EMERGENCY_STATE"] = marker,
        };
        var injector = new SendInputService();
        try
        {
            await using var client = await Connect(env);
            var call = client.CallToolAsync("computer_type",
                new Dictionary<string, object?>
                {
                    ["target"] = new Dictionary<string, object?>
                    {
                        ["hwnd"] = $"0x{_fx.Hwnd:X}",
                        ["role"] = "edit",
                        ["name"] = "text",
                    },
                    ["text"] = new string('x', 8000), // long enough to be mid-flight
                });
            await Task.Delay(250);
            Inject(injector, PanicChord);
            var res = await call;
            Assert.True(res.IsError, "type should abort under panic: " + TextOf(res));
            await AssertServerInputClean(client);
            Inject(injector, ResumeChord);
        }
        finally
        {
            try { Inject(injector, ResumeChord); } catch { }
            try { File.Delete(marker); } catch { }
        }
    }

    /// <summary>computer_reset_input is allowed while EmergencyStopped and
    /// reports the server's owned-input ledger — assert it is empty.</summary>
    private static async Task AssertServerInputClean(McpClient client)
    {
        var reset = await client.CallToolAsync("computer_reset_input",
            new Dictionary<string, object?>());
        Assert.True(reset.IsError != true, TextOf(reset));
        var text = TextOf(reset).Replace(" ", "");
        Assert.Contains("\"ownedKeys\":[]", text);
        Assert.Contains("\"ownedMouseButtons\":[]", text);
        Assert.Contains("\"openTransactions\":[]", text);
    }

    private static async Task<McpClient> Connect(Dictionary<string, string?> env)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "inbrisk-mcp.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [dll],
            EnvironmentVariables = env,
        });
        return await McpClient.CreateAsync(transport);
    }

    private static string TextOf(CallToolResult r)
        => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
}
