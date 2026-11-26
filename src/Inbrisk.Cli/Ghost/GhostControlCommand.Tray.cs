using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Cli.Ui;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Runtime;

namespace Inbrisk.Cli;

public static partial class GhostControlCommand
{
    /// <summary>
    /// Launches the full Ghost OS experience: background session, PiP overlay,
    /// System Tray notification area icon, hotkeys, and remote control server.
    /// </summary>
    public static async Task<int> RunTrayAsync(GhostControlOptions options, CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            LogInfo("Received interrupt signal (Ctrl+C). Initiating graceful Ghost OS shutdown...", options);
            try { linkedCts.Cancel(); } catch { }
        };

        EventHandler processExitHandler = (_, _) =>
        {
            try { linkedCts.Cancel(); } catch { }
        };

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        TraceStartup("RunTray entered");
        LogHeader(options);

        if (EmergencyGate.IsStopped)
        {
            LogWarning("Ghost OS start refused: computer control is stopped by the local emergency hotkey; only the local user can resume.", options);
            return 1;
        }

        GhostLocalDesktop? localDesktop = null;
        GhostDesktopCapture? desktopCapture = null;
        GhostSessionManager? sessionManager = null;
        GhostPipManager? pipManager = null;
        GhostPipExpander? expander = null;
        GhostTrayApp? trayApp = null;
        GhostKeyboardForwarder? keyboardForwarder = null;
        GhostIpcServer? controlServer = null;
        GhostWindowSurfaceManager? surfaceManager = null;
        GhostWorkerDaemon? localWorker = null;
        ActivityHudService? hud = null;
        Mutex? singleInstanceMutex = null;
        bool ownsSingleInstance = false;

        try
        {
            TraceStartup("single-instance mutex begin");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                singleInstanceMutex = new Mutex(false, @"Local\InbriskGhostTrayDaemon");
                try
                {
                    ownsSingleInstance = singleInstanceMutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsSingleInstance = true;
                }

                if (!ownsSingleInstance)
                {
                    TraceStartup("single-instance mutex denied");
                    LogWarning("Ghost OS daemon is already running; refusing to start a duplicate tray process.", options);
                    return 0;
                }
            }
            TraceStartup("single-instance mutex acquired");

            _controlToken = Guid.NewGuid().ToString("N");
            _instanceId = Guid.NewGuid().ToString("N");
            try { File.Delete(ControlStatePath); } catch { }

            // 1. Initialize Isolated Win32 Ghost Desktop. If the canonical
            // object is stale/inaccessible, use a fresh named fallback; never
            // run Ghost capture/input against the user's Default desktop.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                localDesktop = CreateIsolatedDesktop(options);
                if (localDesktop == null)
                {
                    return 1;
                }

                if (!WriteControlState(_controlToken, options.ControlPipeName, localDesktop.DesktopName, "Starting"))
                {
                    LogError("Could not publish Ghost daemon state; refusing to run an unowned isolated session.", options);
                    return 1;
                }
                TraceStartup($"control state published status=Starting desktop={localDesktop.DesktopName}");
                LogSuccess($"Isolated Ghost Desktop active: '{localDesktop.DesktopName}' (Handle: 0x{localDesktop.DesktopHandle:X}, New: {localDesktop.WasCreatedNew})", options);
            }

            // 1b. Secondary RDP Session (enabled by default via StartSession, or explicitly via --extra-session)
            if ((options.StartSession || options.ExtraSession) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LogInfo($"Starting Ghost OS session for account ({options.Username})...", options);
                sessionManager = new GhostSessionManager();

                try
                {
                    var status = await sessionManager.StartSessionAsync(new GhostSessionConfig
                    {
                        Username = options.Username,
                        AutoProvision = true,
                        TimeoutSeconds = 25
                    }, linkedCts.Token).ConfigureAwait(false);

                    if (status.IsActive)
                    {
                        LogSuccess($"Secondary RDP ghost session active (SessionId: {status.SessionId}, State: {status.State})", options);
                    }
                    else
                    {
                        LogWarning($"Secondary session started with state '{status.State}'. Proceeding with PiP overlay...", options);
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"Could not auto-start headless session ({ex.Message}). Continuing with local Ghost Desktop...", options);
                }
            }

            // 2. Initialize Picture-in-Picture (PiP) Display Manager
            byte initialOpacity = options.Opacity ?? 255;
            bool initialInteractive = options.Interactive ?? false;
            PipPresetPosition initialPosition = options.Position ?? PipPresetPosition.TopRight;
            float initialScale = options.Scale ?? 1.0f;

            var pipConfig = new PipConfig(
                Position: initialPosition,
                Scale: initialScale,
                Interactive: initialInteractive,
                Opacity: initialOpacity,
                TargetFps: options.TargetFps,
                Title: "Inbrisk Ghost PiP");

            pipManager = new GhostPipManager(
                ghostDesktopName: localDesktop?.DesktopName ?? GhostLocalDesktop.DefaultDesktopName);
            pipManager.StartPip(pipConfig);
            surfaceManager = new GhostWindowSurfaceManager(pipManager);

            LogSuccess($"Ghost PiP window started ({pipManager.Width}x{pipManager.Height} at {pipManager.CurrentPosition}, HWND: 0x{pipManager.WindowHandle:X8})", options);

            // 3. Initialize Ghost Desktop Capture Engine (streaming ghost desktop to GhostSharedFrameBuffer)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    IntPtr hDesktop = localDesktop?.DesktopHandle ?? IntPtr.Zero;
                    desktopCapture = hDesktop != IntPtr.Zero
                        ? new GhostDesktopCapture(hDesktop, width: 1920, height: 1080, targetFps: options.TargetFps)
                        : new GhostDesktopCapture(GhostLocalDesktop.DefaultDesktopName, width: 1920, height: 1080, targetFps: options.TargetFps);

                    desktopCapture.Start();
                    LogSuccess("Ghost Desktop screen capture active. Streaming frames to shared frame buffer.", options);
                }
                catch (Exception ex)
                {
                    LogWarning($"Could not start Ghost Desktop screen capture engine: {ex.Message}", options);
                }
            }

            // 4. Optionally start a diagnostic shell on the ghost desktop. This
            // is intentionally off by default: PiP is a live capture surface and
            // does not need a foreground cmd window as its initial app.
            if (localDesktop != null && localDesktop.IsCreated && options.StartInitialShell)
            {
                try
                {
                    var (hasActiveApp, consolePids) = GhostDesktopNative.InspectActiveDesktopWindows(localDesktop.DesktopHandle);

                    if (consolePids.Count > 1)
                    {
                        for (int i = 0; i < consolePids.Count - 1; i++)
                        {
                            try
                            {
                                var p = Process.GetProcessById(consolePids[i]);
                                p.Kill(entireProcessTree: true);
                            }
                            catch { }
                        }
                    }

                    if (!hasActiveApp)
                    {
                        var initialProc = localDesktop.StartProcess("cmd.exe", "/k title Inbrisk Ghost Desktop && echo Inbrisk Ghost OS Desktop Active.");
                        LogSuccess($"Started diagnostic terminal on Ghost Desktop (PID: {initialProc.Id}).", options);
                    }
                    else
                    {
                        LogSuccess("Active terminal or application already running on Ghost Desktop. Reusing existing session canvas.", options);
                    }
                }
                catch (Exception ex)
                {
                    LogInfo($"Ghost Desktop canvas active ({ex.Message}).", options);
                }
            }

            // 5. Initialize Full-Screen Expander (Shadow Mode)
            expander = new GhostPipExpander(pipManager.Renderer);

            // 4. Initialize Windows System Tray Icon Application
            trayApp = new GhostTrayApp(initialStatus: "🟢 Inbrisk: Hazır", visible: true);
            trayApp.SetInteractive(initialInteractive);
            trayApp.SetOpacity(initialOpacity);
            trayApp.OnOpenSettings += () =>
            {
                try
                {
                    var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
                    if (!File.Exists(exe))
                        exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
                    Process.Start(new ProcessStartInfo(exe, "control") { UseShellExecute = true });
                }
                catch { }
            };
            trayApp.OnTrayIconClicked += () => trayApp.OpenSettings();
            trayApp.Start();

            if (!trayApp.WaitForReady(3000))
            {
                LogWarning("System Tray message loop took longer than expected to initialize.", options);
            }
            else
            {
                LogSuccess("System Tray icon active in Windows taskbar notification area.", options);
            }

            // 4b. Initialize "Inbrisk Hazır" Taskbar Winbar HUD Pill
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var userSettings = UserSettings.Load();
                    hud = new ActivityHudService(
                        enabled: true,
                        animationsEnabled: userSettings.AnimationsEnabled,
                        alwaysVisible: true,
                        hudDockGapDip: userSettings.HudDockGapDip);
                    hud.Start();
                    hud.SetStandby("Inbrisk Hazır");
                    hud.OnClick = () => trayApp.OpenSettings();
                    LogSuccess("Inbrisk Hazır Winbar HUD pill active on taskbar.", options);
                }
                catch (Exception ex)
                {
                    LogWarning($"Could not initialize Inbrisk Hazır Winbar HUD: {ex.Message}", options);
                }
            }

            // 5. Initialize Keyboard Forwarder & Global Hotkey Listener
            try
            {
                keyboardForwarder = new GhostKeyboardForwarder(ipcBus: null, pipManager: pipManager, targetHwnd: pipManager.WindowHandle, autoStart: true);
                LogSuccess("Global hotkeys registered: Ctrl+Alt+I (Interactive), Ctrl+Shift+P (Visibility), Esc (Restore)", options);
            }
            catch (Exception ex)
            {
                LogWarning($"Failed to initialize global keyboard forwarder: {ex.Message}", options);
            }

            // 6. Wire Synchronized Orchestration Events
            WireOrchestrationEvents(pipManager, expander, trayApp, keyboardForwarder, linkedCts, options);

            // 7. Start Remote Control IPC Server
            controlServer = new GhostIpcServer(options.ControlPipeName);
            RegisterIpcControlHandlers(controlServer, pipManager, expander, trayApp, keyboardForwarder, sessionManager, localDesktop, surfaceManager, linkedCts, options);
            await controlServer.StartAsync(linkedCts.Token).ConfigureAwait(false);
            if (!WriteControlState(_controlToken, options.ControlPipeName,
                    localDesktop?.DesktopName ?? GhostDesktopNative.DefaultGhostDesktopName, "Ready"))
            {
                LogError("Control IPC is running but Ghost daemon readiness could not be published.", options);
                return 1;
            }
            TraceStartup("control state published status=Ready");
            LogSuccess($"Control IPC server listening on \\\\.\\pipe\\{options.ControlPipeName}", options);

            // 8. If fullscreen requested on initial launch, expand now
            if (options.FullScreen == true && pipManager.WindowHandle != IntPtr.Zero)
            {
                expander.ExpandToFullScreen(pipManager.WindowHandle, 0, false);
                trayApp.SetFullScreen(true);
                keyboardForwarder?.NotifyFullscreen(true);
                LogInfo("PiP expanded to Full-Screen Shadow Mode.", options);
            }

            LogInfo("Ghost OS background service is running. Right-click the system tray icon to control.", options);

            // 9. Main supervision loop
            while (!linkedCts.Token.IsCancellationRequested && trayApp.IsRunning)
            {
                try
                {
                    await Task.Delay(1000, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit requested
        }
        catch (Exception ex)
        {
            LogError($"Ghost OS host fatal error: {ex.Message}", options);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;

            DeleteControlStateIfOurs();
            _controlToken = null;

            LogInfo("Shutting down Ghost OS components cleanly...", options);

            // Teardown in reverse dependency order
            if (controlServer != null)
            {
                try { await controlServer.DisposeAsync(); } catch { }
            }

            if (keyboardForwarder != null)
            {
                try { keyboardForwarder.Dispose(); } catch { }
            }

            if (hud != null)
            {
                try { hud.Dispose(); } catch { }
            }

            if (trayApp != null)
            {
                try { trayApp.Dispose(); } catch { }
            }

            if (surfaceManager != null)
            {
                try { surfaceManager.Dispose(); } catch { }
            }

            if (desktopCapture != null)
            {
                try
                {
                    desktopCapture.Stop();
                    desktopCapture.Dispose();
                }
                catch { }
            }

            if (pipManager != null)
            {
                try { pipManager.Dispose(); } catch { }
            }

            if (localDesktop != null)
            {
                try
                {
                    localDesktop.TerminateAllProcesses(1500);
                    localDesktop.Dispose();
                }
                catch { }
            }

            if (localWorker != null)
            {
                try
                {
                    await localWorker.StopAsync().ConfigureAwait(false);
                    localWorker.Dispose();
                }
                catch { }
            }

            if (sessionManager != null)
            {
                try
                {
                    await sessionManager.StopSessionAsync(null, CancellationToken.None).ConfigureAwait(false);
                    sessionManager.Dispose();
                }
                catch { }
            }

            // Thorough cleanup belongs only to the daemon instance that owns
            // the singleton. A duplicate launch must not tear down shared or
            // account-level resources for the already-running owner.
            if (ownsSingleInstance)
            {
                try
                {
                    await GhostRdpConnector.CleanupAllSessionsAsync(options.Username).ConfigureAwait(false);
                }
                catch { }
            }

            if (ownsSingleInstance)
            {
                try { singleInstanceMutex?.ReleaseMutex(); } catch { }
            }
            try { singleInstanceMutex?.Dispose(); } catch { }

            LogSuccess("Ghost OS System Tray session terminated and all resources cleaned up.", options);
        }

        return 0;
    }

    private static void WireOrchestrationEvents(
        GhostPipManager pipManager,
        GhostPipExpander expander,
        GhostTrayApp trayApp,
        GhostKeyboardForwarder? keyboardForwarder,
        CancellationTokenSource linkedCts,
        GhostControlOptions options)
    {
        // A. Tray: Interactivity Toggled
        trayApp.OnInteractiveToggled += (interactive) =>
        {
            pipManager.SetInteractive(interactive);
            LogInfo($"[Tray] PiP interactive mode {(interactive ? "ENABLED (Clickable)" : "DISABLED (Click-through)")}", options);
        };

        // B. Tray: Full-Screen Toggled
        trayApp.OnFullScreenToggled += (fullScreen) =>
        {
            if (pipManager.WindowHandle != IntPtr.Zero)
            {
                expander.ToggleFullScreen(pipManager.WindowHandle, 0, false);
                trayApp.SetFullScreen(expander.IsFullScreen);
                keyboardForwarder?.NotifyFullscreen(expander.IsFullScreen);
                LogInfo($"[Tray] Full-screen mode {(expander.IsFullScreen ? "EXPANDED" : "RESTORED")}", options);
            }
        };

        // C. Tray: Visibility Toggled
        trayApp.OnVisibilityToggled += (visible) =>
        {
            var hwnd = pipManager.WindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                Win32.ShowWindow(hwnd, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
                LogInfo($"[Tray] PiP display {(visible ? "SHOWN" : "HIDDEN")}", options);
            }
        };

        // D. Tray: Opacity Changed
        trayApp.OnOpacityChanged += (alpha) =>
        {
            pipManager.SetOpacity(alpha);
            int pct = (int)Math.Round(alpha * 100.0 / 255.0);
            LogInfo($"[Tray] PiP opacity set to {pct}% (alpha: {alpha})", options);
        };

        // E. Tray: Stop Session Requested
        trayApp.OnStopRequested += () =>
        {
            LogInfo("[Tray] Stop Session requested from System Tray menu.", options);
            try { linkedCts.Cancel(); } catch { }
        };

        // F. Tray: Exit Application Requested
        trayApp.OnExitRequested += () =>
        {
            LogInfo("[Tray] Exit requested from System Tray menu.", options);
            try { linkedCts.Cancel(); } catch { }
        };

        // F2. Tray: Open Settings Requested
        trayApp.OnOpenSettings += () =>
        {
            LogInfo("[Tray] Opening Inbrisk Settings...", options);
        };

        // G. Keyboard Forwarder events
        if (keyboardForwarder != null)
        {
            keyboardForwarder.InteractiveModeToggled += (interactive) =>
            {
                trayApp.SetInteractive(interactive);
                pipManager.SetInteractive(interactive);
                LogInfo($"[Hotkey] Interactive mode toggled to: {(interactive ? "ON" : "OFF")}", options);
            };

            keyboardForwarder.VisibilityToggled += (visible) =>
            {
                trayApp.SetVisible(visible);
                var hwnd = pipManager.WindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    Win32.ShowWindow(hwnd, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
                }
                LogInfo($"[Hotkey] PiP visibility toggled: {(visible ? "VISIBLE" : "HIDDEN")}", options);
            };

            keyboardForwarder.EscapePressed += () =>
            {
                if (expander.IsFullScreen && pipManager.WindowHandle != IntPtr.Zero)
                {
                    expander.ShrinkToPip(pipManager.WindowHandle);
                    trayApp.SetFullScreen(false);
                    LogInfo("[Hotkey] Esc pressed: Restored PiP from full-screen Shadow Mode.", options);
                }
            };

            keyboardForwarder.FullscreenExitRequested += () =>
            {
                if (expander.IsFullScreen && pipManager.WindowHandle != IntPtr.Zero)
                {
                    expander.ShrinkToPip(pipManager.WindowHandle);
                    trayApp.SetFullScreen(false);
                    LogInfo("[Hotkey] Exited full-screen Shadow Mode.", options);
                }
            };
        }
    }

    private static void RegisterIpcControlHandlers(
        GhostIpcServer server,
        GhostPipManager pipManager,
        GhostPipExpander expander,
        GhostTrayApp trayApp,
        GhostKeyboardForwarder? keyboardForwarder,
        GhostSessionManager? sessionManager,
        GhostLocalDesktop? localDesktop,
        GhostWindowSurfaceManager? surfaceManager,
        CancellationTokenSource linkedCts,
        GhostControlOptions options)
    {
        // 1. pip_interactive handler
        server.RegisterHandler("pip_interactive", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }
            bool targetVal;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("interactive", out var prop))
                {
                    targetVal = prop.GetBoolean();
                }
                else
                {
                    targetVal = !pipManager.IsInteractive;
                }
            }
            catch
            {
                targetVal = !pipManager.IsInteractive;
            }

            pipManager.SetInteractive(targetVal);
            trayApp.SetInteractive(targetVal);
            LogInfo($"[IPC] PiP interactivity set to: {(targetVal ? "ON" : "OFF")}", options);

            return JsonSerializer.Serialize(new { ok = true, interactive = targetVal });
        });

        // 2. pip_fullscreen handler
        server.RegisterHandler("pip_fullscreen", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }
            bool isFs = false;
            if (pipManager.WindowHandle != IntPtr.Zero)
            {
                bool? requested = null;
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("fullscreen", out var prop))
                    {
                        requested = prop.GetBoolean();
                    }
                }
                catch { }

                if (requested.HasValue)
                {
                    if (requested.Value && !expander.IsFullScreen)
                    {
                        expander.ExpandToFullScreen(pipManager.WindowHandle, 0, false);
                    }
                    else if (!requested.Value && expander.IsFullScreen)
                    {
                        expander.ShrinkToPip(pipManager.WindowHandle);
                    }
                }
                else
                {
                    expander.ToggleFullScreen(pipManager.WindowHandle, 0, false);
                }

                isFs = expander.IsFullScreen;
                trayApp.SetFullScreen(isFs);
                keyboardForwarder?.NotifyFullscreen(isFs);
                LogInfo($"[IPC] PiP full-screen mode toggled to: {(isFs ? "ON" : "OFF")}", options);
            }

            return JsonSerializer.Serialize(new { ok = true, fullScreen = isFs });
        });

        // 3. pip_position handler
        server.RegisterHandler("pip_position", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }
            string posName = "TopRight";
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("position", out var prop))
                {
                    posName = prop.GetString() ?? "TopRight";
                }
            }
            catch { }

            if (Enum.TryParse<PipPresetPosition>(posName, true, out var preset))
            {
                pipManager.SetPosition(preset);
                LogInfo($"[IPC] PiP position updated to: {preset}", options);
                return JsonSerializer.Serialize(new { ok = true, position = preset.ToString() });
            }

            return JsonSerializer.Serialize(new { ok = false, error = $"Unknown position preset: {posName}" });
        });

        // 4. pip_opacity handler
        server.RegisterHandler("pip_opacity", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }
            byte alpha = 255;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("opacity", out var prop))
                {
                    alpha = prop.GetByte();
                }
            }
            catch { }

            pipManager.SetOpacity(alpha);
            trayApp.SetOpacity(alpha);
            int pct = (int)Math.Round(alpha * 100.0 / 255.0);
            LogInfo($"[IPC] PiP opacity updated to: {pct}% (alpha: {alpha})", options);

            return JsonSerializer.Serialize(new { ok = true, opacity = alpha, percentage = pct });
        });

        // 5. pip_scale handler
        server.RegisterHandler("pip_scale", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }
            float scale = 1.0f;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("scale", out var prop))
                {
                    scale = (float)prop.GetDouble();
                }
            }
            catch { }

            scale = Math.Clamp(scale, 0.25f, 5.0f);
            pipManager.SetScale(scale);
            LogInfo($"[IPC] PiP scale updated to: {scale:F2}x", options);

            return JsonSerializer.Serialize(new { ok = true, scale = pipManager.CurrentScale, width = pipManager.Width, height = pipManager.Height });
        });

        // 6. real-window surface launch/status/input handlers
        server.RegisterHandler("ghost_surface_launch", async (payloadJson) =>
        {
            if (!IsAuthorized(payloadJson))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }

            if (surfaceManager == null)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "surface manager unavailable" });
            }

            string? program = null;
            string? arguments = null;
            string? workingDirectory = null;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("program", out var p)) program = p.GetString();
                if (doc.RootElement.TryGetProperty("args", out var a)) arguments = a.GetString();
                if (doc.RootElement.TryGetProperty("workingDir", out var wd)) workingDirectory = wd.GetString();
            }
            catch { }

            if (string.IsNullOrWhiteSpace(program))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "program required" });
            }

            try
            {
                var surface = await surfaceManager.LaunchAsync(program, arguments, workingDirectory).ConfigureAwait(false);
                Win32.GetWindowThreadProcessId(surface.Hwnd, out var pid);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    backend = "dwmWindowSurface",
                    hwnd = $"0x{surface.Hwnd.ToInt64():X}",
                    pid,
                    title = GetWindowTextSafe(surface.Hwnd)
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        });

        server.RegisterHandler("ghost_surface_status", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }

            var windows = surfaceManager?.GetSurfaceStatus() ?? Array.Empty<object>();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                backend = "dwmWindowSurface",
                activeHwnd = surfaceManager?.ActiveHwnd is { } h && h != IntPtr.Zero ? $"0x{h.ToInt64():X}" : null,
                count = windows.Count,
                windows
            });
        });

        server.RegisterHandler("ghost_surface_select", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson) || surfaceManager == null)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }

            long hwndValue = 0;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("hwnd", out var h))
                {
                    var raw = h.GetString() ?? string.Empty;
                    hwndValue = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToInt64(raw[2..], 16)
                        : Convert.ToInt64(raw);
                }
            }
            catch { }

            var hwnd = new IntPtr(hwndValue);
            bool ok = hwndValue != 0 && surfaceManager.Select(hwnd);
            return JsonSerializer.Serialize(new { ok, hwnd = $"0x{hwndValue:X}" });
        });

        server.RegisterHandler("ghost_surface_click", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson) || surfaceManager == null)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }

            int x = 0, y = 0;
            bool doubleClick = false;
            MouseButton button = MouseButton.Left;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("x", out var px)) x = px.GetInt32();
                if (doc.RootElement.TryGetProperty("y", out var py)) y = py.GetInt32();
                if (doc.RootElement.TryGetProperty("doubleClick", out var dc)) doubleClick = dc.GetBoolean();
                if (doc.RootElement.TryGetProperty("button", out var pb))
                {
                    button = (pb.GetString() ?? "left").ToLowerInvariant() switch
                    {
                        "right" => MouseButton.Right,
                        "middle" => MouseButton.Middle,
                        _ => MouseButton.Left
                    };
                }
            }
            catch { }

            bool ok = surfaceManager.ForwardMouse(
                new PipInputEventArgs(doubleClick ? PipInputEventType.DoubleClick : PipInputEventType.MouseDown, 0, 0, button), x, y);
            if (!doubleClick)
            {
                ok &= surfaceManager.ForwardMouse(new PipInputEventArgs(PipInputEventType.MouseUp, 0, 0, button), x, y);
            }
            return JsonSerializer.Serialize(new { ok, x, y, button = button.ToString(), doubleClick });
        });

        server.RegisterHandler("ghost_surface_type", async (payloadJson) =>
        {
            await Task.Yield();
            if (!IsAuthorized(payloadJson) || surfaceManager == null)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }

            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("text", out var t)) text = t.GetString();
            }
            catch { }

            bool ok = true;
            foreach (var c in text ?? string.Empty)
            {
                ok &= surfaceManager.ForwardKey(new PipInputEventArgs(PipInputEventType.Char, 0, 0, character: c));
            }
            return JsonSerializer.Serialize(new { ok, chars = text?.Length ?? 0 });
        });

        // 7. status handler
        server.RegisterHandler("status", async (_) =>
        {
            var sessionStatus = sessionManager != null
                ? await sessionManager.GetStatusAsync().ConfigureAwait(false)
                : GhostSessionStatus.Inactive;
            bool localDesktopActive = localDesktop?.IsCreated == true;

            var result = new
            {
                ok = true,
                session = new
                {
                    active = sessionStatus.IsActive || localDesktopActive,
                    sessionId = sessionStatus.SessionId,
                    username = sessionStatus.Username ?? (localDesktopActive ? Environment.UserName : null),
                    uptimeSeconds = sessionStatus.Uptime.TotalSeconds,
                    state = sessionStatus.IsActive
                        ? sessionStatus.State
                        : (localDesktopActive ? "LocalDesktop" : sessionStatus.State),
                    backend = sessionStatus.IsActive ? "rdp" : (localDesktopActive ? "localDesktop" : "none"),
                    desktop = localDesktop?.DesktopName
                },
                pip = new
                {
                    running = pipManager.IsRunning,
                    interactive = pipManager.IsInteractive,
                    fullScreen = expander.IsFullScreen,
                    position = pipManager.CurrentPosition.ToString(),
                    scale = pipManager.CurrentScale,
                    width = pipManager.Width,
                    height = pipManager.Height,
                    opacity = pipManager.CurrentConfig.Opacity,
                    hwnd = $"0x{pipManager.WindowHandle:X8}"
                },
                tray = new
                {
                    running = trayApp.IsRunning,
                    visible = trayApp.IsVisible,
                    statusText = trayApp.StatusText
                },
                surface = new
                {
                    backend = "dwmWindowSurface",
                    activeHwnd = surfaceManager?.ActiveHwnd is { } h && h != IntPtr.Zero ? $"0x{h.ToInt64():X}" : null,
                    windows = surfaceManager?.GetSurfaceStatus() ?? Array.Empty<object>()
                }
            };

            return JsonSerializer.Serialize(result);
        });

        // 7. stop handler
        server.RegisterHandler("stop", async (stopReq) =>
        {
            await Task.Yield();
            if (!IsAuthorized(stopReq))
            {
                return JsonSerializer.Serialize(new { ok = false, error = "unauthorized" });
            }
            LogInfo("[IPC] Remote stop request received via IPC pipe.", options);
            _ = Task.Run(async () =>
            {
                await Task.Delay(200).ConfigureAwait(false);
                try { linkedCts.Cancel(); } catch { }
            });
            return JsonSerializer.Serialize(new { ok = true, stopping = true });
        });
    }
}
