using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Inbrisk.Cli;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Platform.Windows.Indicator;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Tray;
using Inbrisk.Runtime;
using Inbrisk.Sdk;
using GhostBatchResult = Inbrisk.Runtime.GhostBatchResult;
using GhostInputExecutor = Inbrisk.Runtime.GhostInputExecutor;

namespace Inbrisk.Cli.Commands;

internal static partial class CliCommands
{

    internal static int RunDesktopShell()
    {
        Inbrisk.Platform.Windows.Native.DesktopBridge.TrySwitchCurrentThread();
        var settings = UserSettings.Load();
        using var control = EmergencyControl.Process;
        control.StartHotkeys(settings.PanicHotkey, settings.ResumeHotkey);

        using var hud = new ActivityHudService(
            enabled: settings.HudEnabled,
            animationsEnabled: settings.AnimationsEnabled,
            alwaysVisible: settings.HudAlwaysVisible,
            hudDockGapDip: settings.HudDockGapDip);
        hud.Start();
        hud.OnClick = () =>
        {
            try
            {
                var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
                if (!File.Exists(exe))
                    exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
                Process.Start(new ProcessStartInfo(exe, "control") { UseShellExecute = true });
            }
            catch { }
        };

        var idleC = UserSettings.ParseColor(settings.IdleColor);
        var emC = UserSettings.ParseColor(settings.EmergencyColor);
        var palette = idleC == null && emC == null
            ? null
            : GlowPalette.FromColors(
                idleC ?? (GlowPalette.Default.R, GlowPalette.Default.G, GlowPalette.Default.B),
                emC ?? (GlowPalette.Default.EmR, GlowPalette.Default.EmG, GlowPalette.Default.EmB));
        using var indicator0 = new ScreenIndicatorService(
            enabled: settings.PerimeterEnabled, palette: palette);
        var indicator = indicator0;
        indicator.Start();

        using var tray = new TrayIconService(settings);
        tray.OnOpenSettings = () =>
        {
            try
            {
                var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
                if (!File.Exists(exe))
                    exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
                Process.Start(new ProcessStartInfo(exe, "control") { UseShellExecute = true });
            }
            catch { }
        };
        tray.OnEmergencyStop = () =>
        {
            control.TriggerLocalPanic("tray icon");
            hud.SetEmergency(true, settings.PanicHotkey ?? "Ctrl+Alt+Pause", settings.ResumeHotkey ?? "Ctrl+Alt+Shift+Pause");
            indicator.SetState(IndicatorState.EmergencyStopped);
        };
        tray.OnResume = () =>
        {
            control.TriggerLocalResume("tray icon");
            hud.SetEmergency(false);
            indicator.SetState(IndicatorState.Disconnected);
            if (settings.HudAlwaysVisible && settings.HudEnabled)
                hud.SetStandby("Inbrisk Hazır");
        };
        tray.OnToggleHud = enabled =>
        {
            var s = UserSettings.Load();
            s.HudEnabled = enabled;
            s.Save();
            hud.SetEnabled(enabled);
            if (enabled && s.HudAlwaysVisible)
                hud.SetStandby("Inbrisk Hazır");
        };
        tray.OnTogglePerimeter = enabled =>
        {
            var s = UserSettings.Load();
            s.PerimeterEnabled = enabled;
            s.Save();
            indicator.SetEnabled(enabled);
        };
        tray.OnPreviewHud = () =>
        {
            Task.Run(async () =>
            {
                try
                {
                    hud.SetEnabled(true);
                    hud.SetActivity("Spotify açılıyor…", "Spotify");
                    await Task.Delay(2200);
                    hud.SetSuccess("Spotify açıldı");
                }
                catch { /* preview is best-effort — never fault an unobserved task */ }
            });
        };

        var quitEvent = new ManualResetEventSlim();
        tray.OnQuit = () =>
        {
            quitEvent.Set();
        };
        tray.Start();

        var settingsSync = new System.Threading.Timer(_ =>
        {
            try
            {
                var s = UserSettings.LoadCached();
                var changed = false;
                if (s.PerimeterEnabled != indicator.Enabled) { indicator.SetEnabled(s.PerimeterEnabled); changed = true; }
                if (s.HudEnabled != hud.Enabled) { hud.SetEnabled(s.HudEnabled); changed = true; }
                hud.SetAnimationsEnabled(s.AnimationsEnabled);
                if (changed)
                    tray.UpdateStatus(mcpConnected: false, clientName: "Standby",
                        working: false, emergency: EmergencyGate.IsStopped,
                        hudEnabled: s.HudEnabled, perimeterEnabled: s.PerimeterEnabled);
            }
            catch { }
        }, null, 1500, 1500);

        control.StateChanged += s =>
        {
            var isEmergency = s == ComputerControlState.EmergencyStopped;
            hud.SetEmergency(isEmergency, settings.PanicHotkey ?? "Ctrl+Alt+Pause", settings.ResumeHotkey ?? "Ctrl+Alt+Shift+Pause");
            indicator.SetState(isEmergency ? IndicatorState.EmergencyStopped : IndicatorState.Disconnected);
            tray.UpdateStatus(
                mcpConnected: false,
                clientName: "Standby",
                working: false,
                emergency: isEmergency,
                hudEnabled: UserSettings.Load().HudEnabled,
                perimeterEnabled: UserSettings.Load().PerimeterEnabled);
        };

        if (settings.HudAlwaysVisible && settings.HudEnabled)
        {
            hud.SetStandby("Inbrisk Hazır");
        }

        if (!settings.StartMinimized)
        {
            tray.OpenSettings();
        }

        quitEvent.Wait();
        indicator.Dispose(); // disposes whichever instance is current
        return 0;
    }
}
