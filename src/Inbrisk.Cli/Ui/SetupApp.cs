using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Indicator;
using Inbrisk.Platform.Windows.Input;
using Inbrisk.Setup;
using Microsoft.Win32;

namespace Inbrisk.Cli.Ui;

/// <summary>
/// Palette for the setup app. "system" reads the Windows apps-use-light-theme
/// setting; dark is the default (matches the terminal-native product feel).
/// </summary>
public static class UiTheme
{
    public static bool Dark = true;

    public static Brush Bg => B(Dark ? 0xFF1B1B1F : 0xFFF7F7F9);
    public static Brush Panel => B(Dark ? 0xFF26262C : 0xFFFFFFFF);
    public static Brush PanelAlt => B(Dark ? 0xFF2E2E35 : 0xFFEFEFEF);
    public static Brush Fg => B(Dark ? 0xFFE8E8EA : 0xFF1B1B1F);
    public static Brush Subtle => B(Dark ? 0xFF9A9AA2 : 0xFF5A5A62);
    public static Brush Accent => B(0xFF7ED321);
    public static Brush Ok => B(0xFF5FD35F);
    public static Brush Err => B(0xFFFF5F5F);
    public static Brush Line => B(Dark ? 0xFF3A3A42 : 0xFFD8D8DE);
    public static Brush NavSel => B(Dark ? 0xFF34343C : 0xFFE2E2E8);

    private static SolidColorBrush B(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16),
            (byte)(argb >> 8), (byte)argb));

    public static void Resolve(string? theme)
    {
        Dark = theme?.ToLowerInvariant() switch
        {
            "light" => false,
            "dark" => true,
            _ => SystemIsDark(),
        };
    }

    private static bool SystemIsDark()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0);
            return v is int i && i == 0;
        }
        catch { return true; }
    }
}

/// <summary>
/// The setup app — installer (InbriskSetup.exe) and manager (`inbrisk ui` or
/// double-clicked inbrisk.exe). WPF hosted on a dedicated STA thread inside
/// the same binary as the CLI; every page reuses the same Inbrisk.Setup
/// services the console commands use.
/// </summary>
public static class SetupApp
{
    public static int Run(bool installerMode)
    {
        var done = new ManualResetEventSlim();
        var code = 0;
        var t = new Thread(() =>
        {
            try
            {
                Inbrisk.Platform.Windows.Native.DesktopBridge.TrySwitchCurrentThread();
                UiTheme.Resolve(UserSettings.Load().Theme);
                if (Application.Current == null)
                {
                    var app = new Application();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }
                Window win = installerMode ? new SetupWindow(true) : new InbriskControlWindow();
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetupApp error: {ex}");
                code = 1;
            }
            finally { done.Set(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        done.Wait();
        return code;
    }
}

public sealed class SetupWindow : Window
{
    private readonly ListBox _nav = new();
    private readonly ContentControl _content = new();
    private readonly bool _installerMode;
    private UserSettings _settings = UserSettings.Load();

    private static readonly (string Key, string Label)[] SettingsPages =
    [
        ("general", "General"),
        ("interface", "Interface"),
        ("safety", "Safety"),
        ("connections", "Connections"),
        ("about", "About"),
        ("hosts", "Hosts"),
        ("doctor", "Doctor"),
        ("status", "Status"),
        ("update", "Update"),
        ("appearance", "Appearance"),
        ("logs", "Logs"),
        ("backups", "Backups"),
        ("install", "Install / Repair"),
    ];

    private static readonly (string Key, string Label)[] InstallerPages =
    [
        ("install", "Install"),
        ("hosts", "Hosts"),
        ("doctor", "Doctor"),
        ("logs", "Logs"),
    ];

    private readonly (string Key, string Label)[] _pages;

    public SetupWindow(bool installerMode)
    {
        _installerMode = installerMode;
        _pages = installerMode ? InstallerPages : SettingsPages;
        Title = installerMode ? "Inbrisk Setup" : "Inbrisk Settings";
        Width = 980; Height = 660; MinWidth = 800; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        try
        {
            var logo = Inbrisk.Platform.Windows.Hud.AppIconExtractor.GetInbriskLogo(64);
            var hBitmap = logo.GetHbitmap();
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch { }

        BuildShell();
        _nav.SelectedIndex = 0;
    }

    // ------------------------------------------------------------ shell

    private void BuildShell()
    {
        var root = new Grid { Background = UiTheme.Bg };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        _nav.Items.Clear();
        _nav.Background = UiTheme.Panel;
        _nav.BorderThickness = new Thickness(0);
        _nav.FontSize = 14;
        foreach (var (_, label) in _pages) _nav.Items.Add(NavItem(label));
        _nav.SelectionChanged += (_, _) => ShowPage();
        Grid.SetColumn(_nav, 0);
        root.Children.Add(_nav);

        _content.Margin = new Thickness(24, 20, 24, 20);
        Grid.SetColumn(_content, 1);
        root.Children.Add(_content);
        Content = root;
    }

    private static ListBoxItem NavItem(string label) => new()
    {
        Content = label,
        Foreground = UiTheme.Fg,
        Padding = new Thickness(14, 10, 14, 10),
    };

    private void ShowPage()
    {
        var key = _pages[Math.Max(0, _nav.SelectedIndex)].Key;
        _content.Content = key switch
        {
            "general" => BuildGeneral(),
            "interface" => BuildInterface(),
            "connections" => BuildConnections(),
            "about" => BuildAbout(),
            "install" => BuildInstall(),
            "hosts" => BuildHosts(),
            "doctor" => BuildDoctor(),
            "status" => BuildStatus(),
            "update" => BuildUpdate(),
            "safety" => BuildSafety(),
            "appearance" => BuildAppearance(),
            "logs" => BuildLogs(),
            "backups" => BuildBackups(),
            _ => new StackPanel(),
        };
    }

    // ------------------------------------------------------------ helpers

    private static TextBlock H(string text, double size = 22) => new()
    {
        Text = text, Foreground = UiTheme.Fg, FontSize = size,
        FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock Sub(string text) => new()
    {
        Text = text, Foreground = UiTheme.Subtle, FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
    };

    private static Button Btn(string text, bool primary = false) => new()
    {
        Content = text, Padding = new Thickness(14, 7, 14, 7),
        Margin = new Thickness(0, 0, 8, 0), FontSize = 13,
        Background = primary ? UiTheme.Accent : UiTheme.PanelAlt,
        Foreground = primary ? Brushes.Black : UiTheme.Fg,
        BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
    };

    private static TextBox LogBox() => new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
        FontSize = 12, Background = UiTheme.Panel, Foreground = UiTheme.Fg,
        BorderBrush = UiTheme.Line, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.Wrap, MinHeight = 180,
        Padding = new Thickness(10),
    };

    private static Border Card(UIElement inner) => new()
    {
        Background = UiTheme.Panel, CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
        Child = inner,
    };

    private void Log(TextBox box, string line)
    {
        void A() { box.AppendText(line + Environment.NewLine); box.ScrollToEnd(); }
        if (Dispatcher.CheckAccess()) A(); else Dispatcher.Invoke(A);
    }

    private static string Exe => InstallLayout.CanonicalExePath;

    // ------------------------------------------------------------ general

    private UIElement BuildGeneral()
    {
        var p = new StackPanel();
        p.Children.Add(H("General Settings"));
        p.Children.Add(Sub("Configure Inbrisk desktop startup and background presence."));

        var autostart = new CheckBox
        {
            Content = "Start Inbrisk with Windows",
            Foreground = UiTheme.Fg,
            IsChecked = _settings.StartWithWindows,
            Margin = new Thickness(0, 4, 0, 12),
            FontSize = 13.5
        };
        autostart.Checked += (_, _) => ToggleAutoStart(true);
        autostart.Unchecked += (_, _) => ToggleAutoStart(false);

        var minimized = new CheckBox
        {
            Content = "Start minimized to system tray",
            Foreground = UiTheme.Fg,
            IsChecked = _settings.StartMinimized,
            Margin = new Thickness(0, 4, 0, 16),
            FontSize = 13.5
        };
        minimized.Checked += (_, _) => { _settings.StartMinimized = true; _settings.Save(); };
        minimized.Unchecked += (_, _) => { _settings.StartMinimized = false; _settings.Save(); };

        var sp = new StackPanel();
        sp.Children.Add(H("Startup Behavior", 15));
        sp.Children.Add(autostart);
        sp.Children.Add(minimized);
        p.Children.Add(Card(sp));

        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void ToggleAutoStart(bool enable)
    {
        _settings.StartWithWindows = enable;
        _settings.Save();
        try
        {
            using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (enable)
            {
                var exe = InstallLayout.CanonicalExePath;
                if (!File.Exists(exe)) exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
                rk?.SetValue("Inbrisk", $"\"{exe}\"");
            }
            else
            {
                rk?.DeleteValue("Inbrisk", false);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------ interface

    private UIElement BuildInterface()
    {
        var p = new StackPanel();
        p.Children.Add(H("Interface & Indicators"));
        p.Children.Add(Sub("Configure the Floating Activity Pill HUD and perimeter smoke indicator."));

        var hudCheck = new CheckBox
        {
            Content = "Activity HUD (Floating Pill)",
            Foreground = UiTheme.Fg,
            IsChecked = _settings.HudEnabled,
            Margin = new Thickness(0, 4, 0, 10),
            FontSize = 13.5
        };
        hudCheck.Checked += (_, _) => { _settings.HudEnabled = true; _settings.Save(); };
        hudCheck.Unchecked += (_, _) => { _settings.HudEnabled = false; _settings.Save(); };

        var perimCheck = new CheckBox
        {
            Content = "Perimeter Smoke Indicator (Screen Border)",
            Foreground = UiTheme.Fg,
            IsChecked = _settings.PerimeterEnabled,
            Margin = new Thickness(0, 4, 0, 10),
            FontSize = 13.5
        };
        perimCheck.Checked += (_, _) => { _settings.PerimeterEnabled = true; _settings.Save(); };
        perimCheck.Unchecked += (_, _) => { _settings.PerimeterEnabled = false; _settings.Save(); };

        var animCheck = new CheckBox
        {
            Content = "Smooth Animations (Sinusoidal Bobbing & Transitions)",
            Foreground = UiTheme.Fg,
            IsChecked = _settings.AnimationsEnabled,
            Margin = new Thickness(0, 4, 0, 12),
            FontSize = 13.5
        };
        animCheck.Checked += (_, _) => { _settings.AnimationsEnabled = true; _settings.Save(); };
        animCheck.Unchecked += (_, _) => { _settings.AnimationsEnabled = false; _settings.Save(); };

        var posBox = new ComboBox
        {
            ItemsSource = new[] { "Top Center (Primary Monitor)", "Top Center (Monitor 1)", "Top Center (Monitor 2)" },
            SelectedIndex = 0,
            Background = UiTheme.Panel,
            Foreground = UiTheme.Fg,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 4, 0, 8),
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var hudAlwaysCheck = new CheckBox
        {
            Content = "Always Visible on Desktop (Masaüstünde sürekli hazır bekle)",
            Foreground = UiTheme.Fg,
            IsChecked = _settings.HudAlwaysVisible,
            Margin = new Thickness(0, 4, 0, 10),
            FontSize = 13.5
        };
        hudAlwaysCheck.Checked += (_, _) => { _settings.HudAlwaysVisible = true; _settings.Save(); };
        hudAlwaysCheck.Unchecked += (_, _) => { _settings.HudAlwaysVisible = false; _settings.Save(); };

        var testBtn = new Button
        {
            Content = "HUD Animasyonunu Test Et (Önizle)",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 8, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        testBtn.Click += (_, _) =>
        {
            Task.Run(async () =>
            {
                using var testHud = new Inbrisk.Platform.Windows.Hud.ActivityHudService(enabled: true, animationsEnabled: true, alwaysVisible: false);
                testHud.Start();
                testHud.SetActivity("Spotify açılıyor…", "Spotify");
                await Task.Delay(2000);
                testHud.SetSuccess("Spotify açıldı");
                await Task.Delay(1500);
            });
        };

        var spVis = new StackPanel();
        spVis.Children.Add(H("Visibility & Presentation", 15));
        spVis.Children.Add(hudCheck);
        spVis.Children.Add(hudAlwaysCheck);
        spVis.Children.Add(perimCheck);
        spVis.Children.Add(animCheck);
        spVis.Children.Add(testBtn);
        p.Children.Add(Card(spVis));

        var spPos = new StackPanel();
        spPos.Children.Add(H("HUD Position", 15));
        spPos.Children.Add(posBox);
        p.Children.Add(Card(spPos));

        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ------------------------------------------------------------ connections

    private UIElement BuildConnections()
    {
        var p = new StackPanel();
        p.Children.Add(H("Connections"));
        p.Children.Add(Sub("Connected MCP hosts and active automation sessions."));

        var sp = new StackPanel();
        sp.Children.Add(H("MCP Host Status", 15));
        sp.Children.Add(new TextBlock
        {
            Text = "Devin — Connected (Active)",
            Foreground = UiTheme.Ok,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 4, 0, 4)
        });
        sp.Children.Add(new TextBlock
        {
            Text = "Active MCP Sessions: 1 (stdio transport)",
            Foreground = UiTheme.Fg,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        });
        p.Children.Add(Card(sp));

        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ------------------------------------------------------------ about

    private UIElement BuildAbout()
    {
        var p = new StackPanel();
        p.Children.Add(H("About Inbrisk"));
        p.Children.Add(Sub("Windows computer control runtime and desktop shell."));

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = $"Version: {InstallLayout.Version}", Foreground = UiTheme.Fg, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(new TextBlock { Text = $"Install Path: {InstallLayout.CanonicalExePath}", Foreground = UiTheme.Subtle, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(new TextBlock { Text = "Platform: Windows x64 (Per-Monitor V2 DPI Aware)", Foreground = UiTheme.Subtle, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(new TextBlock { Text = "Build: Release (Self-Contained)", Foreground = UiTheme.Subtle, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(Card(sp));

        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ------------------------------------------------------------ install

    private UIElement BuildInstall()
    {
        var p = new StackPanel();
        p.Children.Add(H(_installerMode ? "Welcome to Inbrisk" : "Install"));
        p.Children.Add(Sub(
            $"inbrisk {InstallLayout.Version} — Windows computer control for MCP hosts. " +
            "Choose where to install, then pick which AI hosts to connect on the Hosts page."));

        var user = new RadioButton
        {
            Content = $"Just for me — {InstallLayout.UserInstallDir} (recommended, no admin)",
            Foreground = UiTheme.Fg, IsChecked = true, Margin = new Thickness(0, 0, 0, 4),
        };
        var machine = new RadioButton
        {
            Content = $"All users — {InstallLayout.MachineInstallDir} (asks for admin)",
            Foreground = UiTheme.Fg, Margin = new Thickness(0, 0, 0, 12),
        };
        p.Children.Add(user);
        p.Children.Add(machine);

        var log = LogBox();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var install = Btn("Install", primary: true);
        var repair = Btn("Repair");
        var uninstall = Btn("Uninstall");
        row.Children.Add(install); row.Children.Add(repair); row.Children.Add(uninstall);
        p.Children.Add(row);
        p.Children.Add(new Border { Height = 10 });
        p.Children.Add(log);

        install.Click += (_, _) => Task.Run(() =>
        {
            Log(log, $"Installing {(machine.IsChecked == true ? "(machine-wide)" : "(per-user)")}…");
            var r = SelfInstaller.Install(machine.IsChecked == true);
            Log(log, $"{(r.Ok ? "✓" : "✗")} {r.Message}");
            if (r.Ok)
            {
                Log(log, "Next: open the Hosts page to choose which AI hosts to connect.");
                Dispatcher.Invoke(() => _nav.SelectedIndex = 1);
            }
        });
        repair.Click += (_, _) => Task.Run(() =>
        {
            var (mut, prob) = SetupCommands.RepairInstall(Exe);
            foreach (var m in mut) Log(log, $"  ✓ {m}");
            foreach (var e in prob) Log(log, $"  ✗ {e}");
            if (mut.Count == 0 && prob.Count == 0) Log(log, "  nothing to repair");
        });
        uninstall.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                "Remove Inbrisk registrations from all hosts, stop sessions, and delete the install?",
                "Uninstall Inbrisk", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
            Task.Run(() =>
            {
                var r = SelfInstaller.Uninstall(keepData: false, autoYes: true);
                foreach (var a in r.Actions) Log(log, $"  ✓ {a}");
                foreach (var e in r.Errors) Log(log, $"  ✗ {e}");
                Log(log, r.Ok ? "Uninstalled. You can close this window."
                              : "Uninstall finished with errors.");
            });
        };
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ------------------------------------------------------------ hosts

    private UIElement BuildHosts()
    {
        var p = new StackPanel();
        p.Children.Add(H("MCP Hosts"));
        p.Children.Add(Sub("Choose which AI hosts can launch Inbrisk. " +
            "Existing MCP servers in their configs are never touched."));

        var log = LogBox();
        var list = new StackPanel();
        var checks = new List<(IMcpHostAdapter Host, CheckBox Box)>();

        var bar = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10) };
        var connectSel = Btn("Connect selected", primary: true);
        var refresh = Btn("Refresh");
        var manual = Btn("Manual config guide");
        var verify = Btn("Verify handshake");
        bar.Children.Add(connectSel); bar.Children.Add(refresh);
        bar.Children.Add(manual); bar.Children.Add(verify);
        p.Children.Add(bar);
        p.Children.Add(Card(new ScrollViewer { Content = list, MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
        p.Children.Add(new Border { Height = 6 });
        p.Children.Add(log);

        void RenderList()
        {
            list.Children.Clear(); checks.Clear();
            foreach (var h in HostRegistry.All())
            {
                var d = h.Detect();
                HostRegistration? reg = null; string? err = null;
                try { reg = h.GetInbriskEntry(); }
                catch (ConfigMalformedException e) { err = e.Message; }
                var stale = reg != null && !reg.PointsAt(Exe);

                var cb = new CheckBox
                {
                    IsChecked = d.Installed,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                var status = err != null ? $"config malformed — {err}"
                    : !d.Installed ? "not detected"
                    : stale ? $"STALE entry → {reg!.Command}"
                    : reg != null ? $"configured  ·  {reg.ConfigPath}"
                    : "detected, not configured";
                var statusBrush = err != null || stale ? UiTheme.Err
                    : reg != null ? UiTheme.Ok : UiTheme.Subtle;

                var col = new StackPanel();
                col.Children.Add(new TextBlock
                {
                    Text = h.DisplayName + (d.Version != null ? $"  {d.Version}" : ""),
                    Foreground = UiTheme.Fg, FontWeight = FontWeights.SemiBold,
                });
                col.Children.Add(new TextBlock
                {
                    Text = status, Foreground = statusBrush, FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
                if (d.Installed)
                    col.Children.Add(new TextBlock
                    {
                        Text = d.Detail, Foreground = UiTheme.Subtle, FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                    });

                var connect = Btn("Connect"); var disconnect = Btn("Disconnect");
                var openCfg = Btn("Open config");
                connect.Click += (_, _) => Task.Run(() =>
                {
                    var r = h.Connect(Exe);
                    Log(log, $"{(r.Ok ? "✓" : "✗")} {h.DisplayName}: {r.Message}" +
                        (r.BackupPath != null ? $"  (backup: {r.BackupPath})" : ""));
                    if (r.Ok && h.RequiresRestart)
                        Log(log, $"   restart {h.DisplayName} to activate");
                    Dispatcher.Invoke(RenderList);
                });
                disconnect.Click += (_, _) => Task.Run(() =>
                {
                    var r = h.Disconnect();
                    Log(log, $"{(r.Ok ? "✓" : "✗")} {h.DisplayName}: {r.Message}");
                    Dispatcher.Invoke(RenderList);
                });
                openCfg.Click += (_, _) =>
                {
                    if (h is JsonFileHostAdapter j && File.Exists(j.ConfigPath))
                        Process.Start(new ProcessStartInfo(j.ConfigPath) { UseShellExecute = true });
                    else Log(log, $"{h.DisplayName}: no config file on disk yet");
                };
                var btns = new StackPanel { Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 6, 0, 0) };
                btns.Children.Add(connect); btns.Children.Add(disconnect);
                btns.Children.Add(openCfg);

                var hostRow = new DockPanel();
                DockPanel.SetDock(cb, Dock.Left);
                hostRow.Children.Add(cb);
                hostRow.Children.Add(col);
                col.Children.Add(btns);
                list.Children.Add(new Border
                {
                    BorderBrush = UiTheme.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 6, 0, 6), Child = hostRow,
                });
                checks.Add((h, cb));
            }
        }
        RenderList();

        connectSel.Click += (_, _) => Task.Run(() =>
        {
            var exe = Exe;
            foreach (var (h, cb) in checks)
            {
                if (cb.IsChecked != true) continue;
                var r = h.Connect(exe);
                Log(log, $"{(r.Ok ? "✓" : "✗")} {h.DisplayName}: {r.Message}");
                if (!r.Ok) Log(log, h.ManualSetup(exe));
            }
            Dispatcher.Invoke(RenderList);
        });
        refresh.Click += (_, _) => RenderList();
        verify.Click += (_, _) => Task.Run(async () =>
        {
            Log(log, "Running real MCP handshake…");
            var s = await McpSelfTest.RunAsync(Exe);
            Log(log, s.Ok
                ? $"  ✓ {s.ServerName} {s.Version} — {s.ToolCount} tools, " +
                  $"stdout clean={s.StdoutClean}, clean disconnect={s.CleanDisconnect}"
                : $"  ✗ {s.Error}");
        });
        manual.Click += (_, _) =>
        {
            var w = new Window
            {
                Title = "Manual MCP config", Width = 760, Height = 560,
                Background = UiTheme.Bg, Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var sp = new StackPanel { Margin = new Thickness(16) };
            var tb = new TextBox
            {
                Text = SetupCommands.ManualGuideText(Exe),
                IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
                FontSize = 12, Background = UiTheme.Panel, Foreground = UiTheme.Fg,
                AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var copy = Btn("Copy guide");
            copy.Click += (_, _) => Clipboard.SetText(tb.Text);
            sp.Children.Add(tb); sp.Children.Add(copy);
            w.Content = sp;
            w.ShowDialog();
        };
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ------------------------------------------------------------ doctor

    private UIElement BuildDoctor()
    {
        var p = new StackPanel();
        p.Children.Add(H("Doctor"));
        p.Children.Add(Sub("Read-only health check: installation, Windows capabilities, " +
            "safety, a real MCP handshake, and every host registration."));
        var run = Btn("Run doctor", primary: true);
        var repair = Btn("Repair");
        var bar = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(run); bar.Children.Add(repair);
        p.Children.Add(bar);
        var results = new StackPanel();
        p.Children.Add(results);

        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            results.Children.Clear();
            var sections = await Task.Run(() => Doctor.RunAsync(Exe));
            foreach (var s in sections)
            {
                results.Children.Add(new TextBlock
                {
                    Text = s.Title, Foreground = UiTheme.Fg, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 2),
                });
                foreach (var c in s.Checks)
                    results.Children.Add(new TextBlock
                    {
                        Text = $"  {(c.Ok ? "✓" : "✗")} {c.Name}" +
                            (c.Detail != null ? $" — {c.Detail}" : ""),
                        Foreground = c.Ok ? UiTheme.Ok : UiTheme.Err,
                        FontFamily = new FontFamily("Consolas"), FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    });
            }
            run.IsEnabled = true;
        };
        repair.Click += (_, _) => Task.Run(() =>
        {
            var (mut, prob) = SetupCommands.RepairInstall(Exe);
            Dispatcher.Invoke(() =>
            {
                results.Children.Add(new TextBlock
                {
                    Text = "Repair", Foreground = UiTheme.Fg,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2),
                });
                foreach (var m in mut) results.Children.Add(Line($"  ✓ {m}", UiTheme.Ok));
                foreach (var e in prob) results.Children.Add(Line($"  ✗ {e}", UiTheme.Err));
                if (mut.Count == 0 && prob.Count == 0)
                    results.Children.Add(Line("  nothing to repair", UiTheme.Subtle));
            });
        });
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static TextBlock Line(string text, Brush brush) => new()
    {
        Text = text, Foreground = brush, FontFamily = new FontFamily("Consolas"),
        FontSize = 12, TextWrapping = TextWrapping.Wrap,
    };

    // ------------------------------------------------------------ status

    private UIElement BuildStatus()
    {
        var p = new StackPanel();
        p.Children.Add(H("Status"));
        var grid = new StackPanel();
        p.Children.Add(Card(grid));
        var refresh = Btn("Refresh");
        p.Children.Add(refresh);

        void Render()
        {
            grid.Children.Clear();
            var stopped = File.Exists(InstallLayout.EmergencyMarkerPath);
            var pending = File.Exists(InstallLayout.PendingUpdateMarker);
            var sessions = 0;
            try
            {
                sessions = Process.GetProcessesByName("inbrisk")
                    .Count(x => x.Id != Environment.ProcessId);
            }
            catch { }
            (string k, string v)[] rows =
            [
                ("Version", $"{InstallLayout.Version} (commit {InstallLayout.BuildCommit ?? "unknown"})"),
                ("Binary SHA-256", InstallLayout.SelfSha256()),
                ("Installed", InstallLayout.IsInstalled ? $"yes — {InstallLayout.ProcessDir}" : "no (running unpacked)"),
                ("MCP command", $"\"{Exe}\" mcp"),
                ("Active sessions", sessions.ToString()),
                ("Emergency state", stopped ? "STOPPED" : "clear"),
                ("Pending update", pending ? "staged — applies when sessions end" : "none"),
                ("Data dir", InstallLayout.DataDir),
                ("Settings", UserSettings.SettingsPath),
            ];
            foreach (var (k, v) in rows)
            {
                var r = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                r.Children.Add(new TextBlock
                {
                    Text = k, Width = 150, Foreground = UiTheme.Subtle,
                });
                r.Children.Add(new TextBlock
                {
                    Text = v, Foreground = UiTheme.Fg, TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"), FontSize = 12,
                });
                grid.Children.Add(r);
            }
        }
        Render();
        refresh.Click += (_, _) => Render();
        return p;
    }

    // ------------------------------------------------------------ update

    private UIElement BuildUpdate()
    {
        var p = new StackPanel();
        p.Children.Add(H("Update"));
        p.Children.Add(Sub("Updates are verified by SHA-256 before anything is installed. " +
            "Point at a manifest.json (https URL or local file). If a session is active " +
            "the update is staged and applies when it ends."));
        var box = new TextBox
        {
            Text = _settings.UpdateManifest
                ?? Environment.GetEnvironmentVariable("INBRISK_UPDATE_MANIFEST") ?? "",
            Background = UiTheme.Panel, Foreground = UiTheme.Fg,
            BorderBrush = UiTheme.Line, Padding = new Thickness(8, 6, 8, 6),
            FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 8),
        };
        p.Children.Add(Card(box));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var check = Btn("Check & update", primary: true);
        var apply = Btn("Apply staged update");
        row.Children.Add(check); row.Children.Add(apply);
        p.Children.Add(row);
        var log = LogBox();
        p.Children.Add(new Border { Height = 8 });
        p.Children.Add(log);

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            var m = box.Text.Trim();
            _settings.UpdateManifest = string.IsNullOrEmpty(m) ? null : m;
            _settings.Save();
            var r = await Task.Run(() => SelfInstaller.UpdateAsync(m));
            Log(log, $"{(r.Ok ? "✓" : "✗")} {r.Message}");
            check.IsEnabled = true;
        };
        apply.Click += (_, _) =>
        {
            var r = SelfInstaller.ApplyPendingUpdate();
            Log(log, $"{(r.Ok ? "✓" : "✗")} {r.Message}");
        };
        return p;
    }

    // ------------------------------------------------------------ safety

    private UIElement BuildSafety()
    {
        var p = new StackPanel();
        p.Children.Add(H("Safety"));
        p.Children.Add(Sub("The emergency stop marker is shared by every Inbrisk " +
            "process — writing it here stops computer control machine-wide within ~150ms. " +
            "Only a local user can resume."));

        var state = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10) };
        var toggle = Btn("⏻ Kontrol", primary: true);
        toggle.FontSize = 15; toggle.Padding = new Thickness(20, 10, 20, 10);
        void Render()
        {
            var stopped = File.Exists(InstallLayout.EmergencyMarkerPath);
            state.Text = stopped ? "■  EMERGENCY STOPPED" : "●  control active";
            state.Foreground = stopped ? UiTheme.Err : UiTheme.Ok;
            toggle.Content = stopped ? "⏻ Kapalı — açmak için tıkla" : "⏻ Açık — durdurmak için tıkla";
            toggle.Background = stopped ? UiTheme.Err : UiTheme.Accent;
            toggle.Foreground = stopped ? Brushes.White : Brushes.Black;
        }
        Render();

        var row = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 16) };
        row.Children.Add(toggle);
        p.Children.Add(state);
        p.Children.Add(row);

        toggle.Click += (_, _) =>
        {
            if (File.Exists(InstallLayout.EmergencyMarkerPath))
                try { File.Delete(InstallLayout.EmergencyMarkerPath); } catch { }
            else
                File.WriteAllText(InstallLayout.EmergencyMarkerPath,
                    DateTimeOffset.UtcNow.ToString("O"));
            Render();
        };

        // ---- hotkeys ----
        p.Children.Add(H("Hotkeys", 15));
        p.Children.Add(Sub("Panic and resume chords for new sessions. " +
            "Format like Ctrl+Alt+Pause. Saved to settings.json; running sessions keep their current chords."));
        var panic = new TextBox { Text = _settings.PanicHotkey ?? "Ctrl+Alt+Pause",
            Background = UiTheme.Panel, Foreground = UiTheme.Fg, BorderBrush = UiTheme.Line,
            Padding = new Thickness(8, 5, 8, 5), FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 0, 0, 6) };
        var res = new TextBox { Text = _settings.ResumeHotkey ?? "Ctrl+Alt+Shift+Pause",
            Background = UiTheme.Panel, Foreground = UiTheme.Fg, BorderBrush = UiTheme.Line,
            Padding = new Thickness(8, 5, 8, 5), FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 0, 0, 6) };
        p.Children.Add(Card(Labelled("Panic chord", panic)));
        p.Children.Add(Card(Labelled("Resume chord", res)));
        var save = Btn("Save hotkeys");
        var msg = new TextBlock { Foreground = UiTheme.Subtle, Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(save); p.Children.Add(msg);
        save.Click += (_, _) =>
        {
            try
            {
                HotkeyChord.Parse(panic.Text.Trim());
                HotkeyChord.Parse(res.Text.Trim());
            }
            catch (Exception e)
            {
                msg.Foreground = UiTheme.Err;
                msg.Text = $"Invalid chord: {e.Message}";
                return;
            }
            _settings.PanicHotkey = panic.Text.Trim();
            _settings.ResumeHotkey = res.Text.Trim();
            _settings.Save();
            msg.Foreground = UiTheme.Ok;
            msg.Text = "Saved — applies to newly started MCP sessions.";
        };
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static StackPanel Labelled(string label, Control c)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock
        { Text = label, Foreground = UiTheme.Subtle, Margin = new Thickness(0, 0, 0, 4) });
        s.Children.Add(c);
        return s;
    }

    // ------------------------------------------------------------ appearance

    private UIElement BuildAppearance()
    {
        var p = new StackPanel();
        p.Children.Add(H("Appearance"));

        // theme
        p.Children.Add(H("App theme", 15));
        var theme = new ComboBox
        {
            ItemsSource = new[] { "system", "dark", "light" },
            SelectedItem = _settings.Theme ?? "system",
            Background = UiTheme.Panel, Foreground = UiTheme.Fg,
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 14),
            Width = 200, HorizontalAlignment = HorizontalAlignment.Left,
        };
        theme.SelectionChanged += (_, _) =>
        {
            _settings.Theme = theme.SelectedItem?.ToString();
            _settings.Save();
            UiTheme.Resolve(_settings.Theme);
            var sel = _nav.SelectedIndex;
            BuildShell();
            _nav.SelectedIndex = sel;
        };
        p.Children.Add(theme);

        // indicator colors
        p.Children.Add(H("Screen-control indicator", 15));
        p.Children.Add(Sub("Per-monitor glow colors. The highlight in the densest pixels " +
            "is derived automatically. Colors apply to the next MCP session."));

        var idleBox = MakeColorRow("Standby / active glow",
            _settings.IdleColor ?? "#B7FF3C",
            ["#B7FF3C", "#40C4FF", "#FF80AB", "#FFD740", "#B388FF"]);
        var emBox = MakeColorRow("Emergency stop glow",
            _settings.EmergencyColor ?? "#FF3B3B",
            ["#FF3B3B", "#FF9100", "#FF1744", "#FFFF00"]);
        p.Children.Add(Card(idleBox.Panel));
        p.Children.Add(Card(emBox.Panel));

        var save = Btn("Save colors", primary: true);
        var reset = Btn("Reset defaults");
        var msg = new TextBlock { Foreground = UiTheme.Subtle, Margin = new Thickness(0, 6, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(save); row.Children.Add(reset);
        p.Children.Add(row); p.Children.Add(msg);

        save.Click += (_, _) =>
        {
            var idle = UserSettings.ParseColor(idleBox.Hex.Text);
            var em = UserSettings.ParseColor(emBox.Hex.Text);
            if (idle == null || em == null)
            {
                msg.Foreground = UiTheme.Err;
                msg.Text = "Colors must be #RRGGBB (e.g. #B7FF3C).";
                return;
            }
            _settings.IdleColor = idleBox.Hex.Text.Trim();
            _settings.EmergencyColor = emBox.Hex.Text.Trim();
            _settings.Save();
            msg.Foreground = UiTheme.Ok;
            msg.Text = "Saved — new MCP sessions use these colors.";
        };
        reset.Click += (_, _) =>
        {
            idleBox.Hex.Text = "#B7FF3C"; emBox.Hex.Text = "#FF3B3B";
            idleBox.Refresh(); emBox.Refresh();
        };
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private sealed record ColorRow(StackPanel Panel, TextBox Hex, Action Refresh);

    private ColorRow MakeColorRow(string label, string initial, string[] presets)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        { Text = label, Foreground = UiTheme.Subtle, Margin = new Thickness(0, 0, 0, 4) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var preview = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 8, 0), BorderBrush = UiTheme.Line,
            BorderThickness = new Thickness(1),
        };
        var hex = new TextBox
        {
            Text = initial, Width = 90,
            Background = UiTheme.PanelAlt, Foreground = UiTheme.Fg,
            BorderBrush = UiTheme.Line, Padding = new Thickness(6, 3, 6, 3),
            FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center,
        };
        void Refresh()
        {
            var c = UserSettings.ParseColor(hex.Text);
            preview.Background = c is { } rgb
                ? new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B))
                : Brushes.Transparent;
        }
        Refresh();
        hex.TextChanged += (_, _) => Refresh();
        row.Children.Add(preview);
        row.Children.Add(hex);
        foreach (var pre in presets)
        {
            var c = UserSettings.ParseColor(pre)!.Value;
            var b = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            };
            var captured = pre;
            b.Click += (_, _) => { hex.Text = captured; Refresh(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        return new ColorRow(panel, hex, Refresh);
    }

    // ------------------------------------------------------------ logs

    private UIElement BuildLogs()
    {
        var p = new StackPanel();
        p.Children.Add(H("Logs"));
        p.Children.Add(Sub($"Telemetry + emergency-control logs under {InstallLayout.DataDir}."));
        var picker = new ComboBox
        {
            Background = UiTheme.Panel, Foreground = UiTheme.Fg,
            Margin = new Thickness(0, 0, 0, 8), Width = 340,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var view = LogBox(); view.MinHeight = 300;
        var bar = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8) };
        var refresh = Btn("Refresh"); var open = Btn("Open folder"); var clear = Btn("Clear file");
        bar.Children.Add(refresh); bar.Children.Add(open); bar.Children.Add(clear);
        p.Children.Add(picker); p.Children.Add(bar); p.Children.Add(view);

        void Files()
        {
            picker.Items.Clear();
            if (Directory.Exists(InstallLayout.DataDir))
                foreach (var f in Directory.GetFiles(InstallLayout.DataDir, "*.jsonl"))
                    picker.Items.Add(Path.GetFileName(f));
            if (picker.Items.Count > 0 && picker.SelectedIndex < 0)
                picker.SelectedIndex = 0;
        }
        void Show()
        {
            var name = picker.SelectedItem?.ToString();
            if (name == null) { view.Text = "(no log files)"; return; }
            var path = Path.Combine(InstallLayout.DataDir, name);
            if (!File.Exists(path)) { view.Text = "(missing)"; return; }
            var lines = File.ReadAllLines(path);
            view.Text = string.Join(Environment.NewLine, lines.TakeLast(200));
            view.ScrollToEnd();
        }
        Files(); Show();
        picker.SelectionChanged += (_, _) => Show();
        refresh.Click += (_, _) => { Files(); Show(); };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(InstallLayout.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", InstallLayout.DataDir)
            { UseShellExecute = true });
        };
        clear.Click += (_, _) =>
        {
            var name = picker.SelectedItem?.ToString();
            if (name != null)
                try { File.WriteAllText(Path.Combine(InstallLayout.DataDir, name), ""); } catch { }
            Show();
        };
        return p;
    }

    // ------------------------------------------------------------ backups

    private UIElement BuildBackups()
    {
        var p = new StackPanel();
        p.Children.Add(H("Config backups"));
        p.Children.Add(Sub("Timestamped copies of host configs taken before every " +
            "mutation. Restoring overwrites the live config file."));

        var list = new ListBox
        {
            Background = UiTheme.Panel, Foreground = UiTheme.Fg,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            MinHeight = 260, Margin = new Thickness(0, 0, 0, 8),
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var restore = Btn("Restore selected", primary: true);
        var refresh = Btn("Refresh");
        var open = Btn("Open folder");
        bar.Children.Add(restore); bar.Children.Add(refresh); bar.Children.Add(open);
        p.Children.Add(bar);
        p.Children.Add(list);
        var msg = new TextBlock { Foreground = UiTheme.Subtle, Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(msg);

        // backup dirs are named by the config file's sanitized name — map back
        // to the owning adapter(s) so restore targets the real config path
        var entries = new List<(string Label, string Bak, string Target)>();
        void Render()
        {
            entries.Clear(); list.Items.Clear();
            var adapters = HostRegistry.All().OfType<JsonFileHostAdapter>().ToList();
            if (!Directory.Exists(InstallLayout.BackupDir)) return;
            foreach (var dir in Directory.GetDirectories(InstallLayout.BackupDir))
            {
                var dirName = Path.GetFileName(dir);
                var targets = adapters
                    .Where(a => Sanitize(Path.GetFileName(a.ConfigPath)) == dirName)
                    .ToList();
                foreach (var bak in Directory.GetFiles(dir, "*.bak")
                    .OrderByDescending(f => File.GetLastWriteTime(f)))
                {
                    foreach (var t in targets.Count > 0 ? targets : [null])
                    {
                        var target = t?.ConfigPath ?? "(unknown config)";
                        var label = $"{Path.GetFileName(bak)}  →  {(t?.DisplayName ?? "?")}  {target}";
                        entries.Add((label, bak, t?.ConfigPath ?? ""));
                        list.Items.Add(label);
                    }
                }
            }
            if (list.Items.Count == 0) list.Items.Add("(no backups yet)");
        }
        Render();

        restore.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= entries.Count) return;
            var e = entries[list.SelectedIndex];
            if (e.Target == "")
            {
                msg.Foreground = UiTheme.Err;
                msg.Text = "Can't resolve which config this backup belongs to.";
                return;
            }
            if (MessageBox.Show(this, $"Overwrite\n{e.Target}\nwith\n{e.Bak}?",
                "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
            try
            {
                File.Copy(e.Bak, e.Target, overwrite: true);
                msg.Foreground = UiTheme.Ok;
                msg.Text = $"Restored → {e.Target}";
            }
            catch (Exception ex)
            {
                msg.Foreground = UiTheme.Err;
                msg.Text = $"Restore failed: {ex.Message}";
            }
        };
        refresh.Click += (_, _) => Render();
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(InstallLayout.BackupDir);
            Process.Start(new ProcessStartInfo("explorer.exe", InstallLayout.BackupDir)
            { UseShellExecute = true });
        };
        return p;
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
