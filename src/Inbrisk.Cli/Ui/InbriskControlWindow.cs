using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Platform.Windows.Native;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Setup;

namespace Inbrisk.Cli.Ui;

/// <summary>
/// Production Main Control Window for the Inbrisk Desktop Control Runtime (Deliverable 1).
/// Modern Windows 11 Obsidian Glass control dashboard inspired by Reference A.
/// Communicates live runtime state, monitored target window, controls, tools, permissions, and settings.
/// </summary>
public sealed class InbriskControlWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly WindowService _windowService = new();
    private UserSettings _settings = UserSettings.Load();

    // UI Structure
    private readonly Border _rootBorder = new();
    private readonly TextBlock _stateBadgeText = new();
    private readonly Ellipse _stateBadgeDot = new();
    private readonly Border _stateBadgeBorder = new();
    private readonly TextBlock _stateMessageText = new();
    private readonly Button _emergencyBtn = new();

    // Tab buttons & containers
    private readonly List<Button> _tabButtons = new();
    private readonly ContentControl _tabContent = new();
    private string _currentTab = "live";

    // Live View Components
    private readonly TextBlock _targetTitleText = new();
    private readonly TextBlock _targetProcessText = new();
    private readonly TextBlock _targetDetailsText = new();
    private readonly Border _targetOwnershipBadge = new();
    private readonly TextBlock _targetOwnershipText = new();
    private readonly Border _viewportSurface = new();
    private readonly Canvas _viewportCanvas = new();
    private readonly TextBlock _liveActionCaption = new();
    private readonly TextBlock _telemetryActionText = new();
    private readonly TextBlock _telemetryCoordsText = new();
    private readonly TextBlock _telemetryLatencyText = new();
    private readonly StackPanel _compactActivityList = new();

    // Activity Stream
    private readonly StackPanel _activityTimelineList = new();

    // Footer items
    private readonly TextBlock _footerMachineText = new();
    private readonly TextBlock _footerSessionText = new();
    private readonly TextBlock _footerRuntimeText = new();
    private readonly TextBlock _footerReadinessText = new();

    // State tracking
    private bool _isEmergency;
    private bool _isActive;
    private string _lastOperation = "Awaiting MCP instructions";
    private string _activeTargetTitle = "Desktop Surface";
    private string _activeTargetProcess = "explorer.exe";
    private int _activeTargetPid;
    private long _activeTargetHwnd;
    private Point _lastActionPoint = new(480, 320);

    // Simulated/Recorded activity feed
    private readonly List<(DateTime Time, string Action, string Target, string Details, string Outcome)> _activityRecords = new();

    public InbriskControlWindow()
    {
        Title = "Inbrisk — Desktop Control Runtime";
        Width = 1040;
        Height = 700;
        MinWidth = 920;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = InbriskTheme.Background;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Topmost = true;

        SeedSampleActivity();
        BuildWindow();
        ApplyWindows11Backdrop();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick += (_, _) => PollRuntimeState();
        _refreshTimer.Start();

        EmergencyControl.Process.StateChanged += state =>
        {
            Dispatcher.Invoke(() =>
            {
                _isEmergency = state == ComputerControlState.EmergencyStopped;
                UpdateStateDisplay();
            });
        };
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void ApplyWindows11Backdrop()
    {
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
            var cornerPref = 2;
            _ = DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));

            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20
            var darkMode = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
        };
    }

    // -------------------------------------------------------------
    // Window Shell Construction
    // -------------------------------------------------------------
    private void BuildWindow()
    {
        _rootBorder.Background = InbriskTheme.Background;
        _rootBorder.BorderBrush = InbriskTheme.BorderMedium;
        _rootBorder.BorderThickness = new Thickness(1);
        _rootBorder.CornerRadius = InbriskTheme.RadiusWindow;

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) }); // Header
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) }); // Top Status & Actions
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) }); // Tab Strip
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); // Footer

        // Row 0: Header
        var header = BuildHeader();
        Grid.SetRow(header, 0);
        rootGrid.Children.Add(header);

        // Row 1: Top Status & Actions Area
        var topBar = BuildTopActionBar();
        Grid.SetRow(topBar, 1);
        rootGrid.Children.Add(topBar);

        // Row 2: Tab Strip
        var tabStrip = BuildTabStrip();
        Grid.SetRow(tabStrip, 2);
        rootGrid.Children.Add(tabStrip);

        // Row 3: Tab Content
        _tabContent.Margin = new Thickness(20, 10, 20, 10);
        Grid.SetRow(_tabContent, 3);
        rootGrid.Children.Add(_tabContent);

        // Row 4: Footer
        var footer = BuildFooter();
        Grid.SetRow(footer, 4);
        rootGrid.Children.Add(footer);

        _rootBorder.Child = rootGrid;
        Content = _rootBorder;

        // Default tab
        SwitchTab("live");
    }

    // -------------------------------------------------------------
    // Section A: Header / Title Area
    // -------------------------------------------------------------
    private UIElement BuildHeader()
    {
        var headerBorder = new Border
        {
            Background = InbriskTheme.Void,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(16, 16, 0, 0),
            Padding = new Thickness(20, 0, 16, 0)
        };
        headerBorder.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: Inbrisk Brand + Descriptor
        var brandPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Ember emblem glyph
        var emberBadge = new Border
        {
            Background = InbriskTheme.AccentSoft,
            BorderBrush = InbriskTheme.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = "✦",
                FontSize = 13,
                Foreground = InbriskTheme.AccentGlow,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        brandPanel.Children.Add(emberBadge);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock
        {
            Text = "INBRISK",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = InbriskTheme.TextPrimary
        };
        var subText = new TextBlock
        {
            Text = "Desktop Control Runtime",
            FontSize = 10,
            Foreground = InbriskTheme.TextMuted,
            Margin = new Thickness(0, -1, 0, 0)
        };
        titleStack.Children.Add(titleText);
        titleStack.Children.Add(subText);
        brandPanel.Children.Add(titleStack);

        // Version badge
        var verBadge = new Border
        {
            Background = InbriskTheme.SurfaceElevated,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusTag,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "v1.0.0 • MCP",
                FontSize = 10,
                Foreground = InbriskTheme.TextSecondary
            }
        };
        brandPanel.Children.Add(verBadge);
        Grid.SetColumn(brandPanel, 0);
        grid.Children.Add(brandPanel);

        // Right: Window controls
        var controlsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var minBtn = CreateChromeButton("—", () => WindowState = System.Windows.WindowState.Minimized);
        var closeBtn = CreateChromeButton("✕", () => Close(), isClose: true);

        Button? pinBtn = null;
        pinBtn = CreateChromeButton("📌", () =>
        {
            Topmost = !Topmost;
            if (pinBtn != null)
            {
                pinBtn.Content = Topmost ? "📌" : "📍";
                pinBtn.ToolTip = Topmost ? "Always on top (Pinned)" : "Normal window order (Unpinned)";
            }
        });
        pinBtn.ToolTip = "Always on top (Pinned)";

        controlsPanel.Children.Add(pinBtn);
        controlsPanel.Children.Add(minBtn);
        controlsPanel.Children.Add(closeBtn);
        Grid.SetColumn(controlsPanel, 2);
        grid.Children.Add(controlsPanel);

        headerBorder.Child = grid;
        return headerBorder;
    }

    private Button CreateChromeButton(string symbol, Action onClick, bool isClose = false)
    {
        var btn = new Button
        {
            Content = symbol,
            Width = 32,
            Height = 28,
            FontSize = 11,
            Foreground = InbriskTheme.TextSecondary,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(3, 0, 0, 0)
        };
        btn.Click += (_, _) => onClick();
        btn.MouseEnter += (_, _) =>
        {
            btn.Background = isClose ? InbriskTheme.StateEmergency : InbriskTheme.SurfaceHover;
            btn.Foreground = isClose ? Brushes.White : InbriskTheme.TextPrimary;
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = Brushes.Transparent;
            btn.Foreground = InbriskTheme.TextSecondary;
        };
        return btn;
    }

    // -------------------------------------------------------------
    // Section B: Top Status & Primary Action Area
    // -------------------------------------------------------------
    private UIElement BuildTopActionBar()
    {
        var container = new Border
        {
            Background = InbriskTheme.Surface,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 12, 20, 12)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: Live Runtime Status
        var statusStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

        _stateBadgeBorder.CornerRadius = InbriskTheme.RadiusTag;
        _stateBadgeBorder.Padding = new Thickness(8, 3, 8, 3);
        _stateBadgeBorder.Margin = new Thickness(0, 0, 10, 0);

        var badgeInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _stateBadgeDot.Width = 8;
        _stateBadgeDot.Height = 8;
        _stateBadgeDot.Margin = new Thickness(0, 0, 6, 0);
        badgeInner.Children.Add(_stateBadgeDot);

        _stateBadgeText.FontSize = 11;
        _stateBadgeText.FontWeight = FontWeights.Bold;
        badgeInner.Children.Add(_stateBadgeText);

        _stateBadgeBorder.Child = badgeInner;
        badgeRow.Children.Add(_stateBadgeBorder);

        // Monitored target badge
        var targetHint = new Border
        {
            Background = InbriskTheme.SurfaceElevated,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusTag,
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = "Target: Windows Desktop",
                FontSize = 11,
                Foreground = InbriskTheme.TextSecondary
            }
        };
        badgeRow.Children.Add(targetHint);
        statusStack.Children.Add(badgeRow);

        // Operational Message
        _stateMessageText.Text = "Awaiting incoming MCP instructions...";
        _stateMessageText.FontSize = 12.5;
        _stateMessageText.Foreground = InbriskTheme.TextPrimary;
        statusStack.Children.Add(_stateMessageText);

        Grid.SetColumn(statusStack, 0);
        grid.Children.Add(statusStack);

        // Right: Action Buttons (Emergency Stop & Pause)
        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Single Master Control Toggle Button
        _emergencyBtn.Content = "⏻ Agent: Açık";
        _emergencyBtn.ToolTip = "Tek tıkla aç/kapat — ajan kontrolünü durdurur veya sürdürür";
        _emergencyBtn.Padding = new Thickness(20, 9, 20, 9);
        _emergencyBtn.Background = InbriskTheme.StateSuccess;
        _emergencyBtn.Foreground = Brushes.White;
        _emergencyBtn.BorderBrush = InbriskTheme.StateSuccessGlow;
        _emergencyBtn.BorderThickness = new Thickness(1);
        _emergencyBtn.FontSize = 13;
        _emergencyBtn.FontWeight = FontWeights.Bold;
        _emergencyBtn.Cursor = Cursors.Hand;
        _emergencyBtn.Click += (_, _) => TriggerEmergencyStop();
        actionsPanel.Children.Add(_emergencyBtn);

        Grid.SetColumn(actionsPanel, 1);
        grid.Children.Add(actionsPanel);

        container.Child = grid;
        UpdateStateDisplay();
        return container;
    }

    // -------------------------------------------------------------
    // Section C: Tab / Navigation Strip
    // -------------------------------------------------------------
    private UIElement BuildTabStrip()
    {
        var bar = new Border
        {
            Background = InbriskTheme.Void,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 6, 20, 6)
        };

        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        _tabButtons.Clear();

        _tabButtons.Add(CreateTabButton("live", "Live View"));
        _tabButtons.Add(CreateTabButton("activity", "Activity Timeline"));
        _tabButtons.Add(CreateTabButton("permissions", "Permissions & Safety"));
        _tabButtons.Add(CreateTabButton("settings", "Settings"));

        foreach (var b in _tabButtons) strip.Children.Add(b);
        bar.Child = strip;
        return bar;
    }

    private Button CreateTabButton(string tabKey, string label)
    {
        var btn = new Button
        {
            Content = label,
            Tag = tabKey,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent,
            Foreground = InbriskTheme.TextSecondary,
            BorderThickness = new Thickness(0),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Cursor = Cursors.Hand
        };
        btn.Click += (_, _) => SwitchTab(tabKey);
        return btn;
    }

    private void SwitchTab(string tabKey)
    {
        _currentTab = tabKey;

        foreach (var b in _tabButtons)
        {
            var isCurrent = (string)b.Tag == tabKey;
            b.Background = isCurrent ? InbriskTheme.SurfaceElevated : Brushes.Transparent;
            b.Foreground = isCurrent ? InbriskTheme.TextPrimary : InbriskTheme.TextSecondary;
            b.FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Medium;
        }

        _tabContent.Content = tabKey switch
        {
            "live" => BuildLiveViewPane(),
            "activity" => BuildActivityTimelinePane(),
            "permissions" => BuildPermissionsPane(),
            "settings" => BuildSettingsPane(),
            _ => BuildLiveViewPane()
        };
    }

    // -------------------------------------------------------------
    // Section D: Live View Panel (Primary Pane)
    // -------------------------------------------------------------
    private UIElement BuildLiveViewPane()
    {
        var grid = new Grid { Background = InbriskTheme.Background };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); // gap
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38, GridUnitType.Star) });

        // Left: Target Window Monitor & Session Viewport
        var leftPanel = new Grid();
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Target Card
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Viewport
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Live Caption

        // Target Application Card
        var targetCard = new Border
        {
            Background = InbriskTheme.Surface,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusPanel,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var targetGrid = new Grid();
        targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var targetInfo = new StackPanel();
        _targetTitleText.Text = _activeTargetTitle;
        _targetTitleText.FontSize = 13.5;
        _targetTitleText.FontWeight = FontWeights.SemiBold;
        _targetTitleText.Foreground = InbriskTheme.TextPrimary;
        targetInfo.Children.Add(_targetTitleText);

        var subInfo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        _targetProcessText.Text = $"{_activeTargetProcess} (PID: {_activeTargetPid})";
        _targetProcessText.FontSize = 11;
        _targetProcessText.Foreground = InbriskTheme.TextSecondary;
        subInfo.Children.Add(_targetProcessText);

        var sep = new TextBlock { Text = " • ", FontSize = 11, Foreground = InbriskTheme.TextMuted };
        subInfo.Children.Add(sep);

        _targetDetailsText.Text = $"HWND: 0x{_activeTargetHwnd:X8} • Bounds: 1920x1080";
        _targetDetailsText.FontSize = 11;
        _targetDetailsText.Foreground = InbriskTheme.TextMuted;
        subInfo.Children.Add(_targetDetailsText);
        targetInfo.Children.Add(subInfo);

        Grid.SetColumn(targetInfo, 0);
        targetGrid.Children.Add(targetInfo);

        // Ownership badge
        _targetOwnershipBadge.Background = InbriskTheme.AccentSoft;
        _targetOwnershipBadge.BorderBrush = InbriskTheme.BorderEmber;
        _targetOwnershipBadge.BorderThickness = new Thickness(1);
        _targetOwnershipBadge.CornerRadius = InbriskTheme.RadiusTag;
        _targetOwnershipBadge.Padding = new Thickness(8, 4, 8, 4);
        _targetOwnershipBadge.VerticalAlignment = VerticalAlignment.Center;
        _targetOwnershipText.Text = "Agent-Owned (Reusable)";
        _targetOwnershipText.FontSize = 10.5;
        _targetOwnershipText.FontWeight = FontWeights.Medium;
        _targetOwnershipText.Foreground = InbriskTheme.TextEmber;
        _targetOwnershipBadge.Child = _targetOwnershipText;

        Grid.SetColumn(_targetOwnershipBadge, 1);
        targetGrid.Children.Add(_targetOwnershipBadge);
        targetCard.Child = targetGrid;
        Grid.SetRow(targetCard, 0);
        leftPanel.Children.Add(targetCard);

        // Viewport Canvas Frame
        _viewportSurface.Background = InbriskTheme.Void;
        _viewportSurface.BorderBrush = InbriskTheme.BorderSubtle;
        _viewportSurface.BorderThickness = new Thickness(1);
        _viewportSurface.CornerRadius = InbriskTheme.RadiusPanel;
        _viewportSurface.ClipToBounds = true;

        var vpGrid = new Grid();
        _viewportCanvas.HorizontalAlignment = HorizontalAlignment.Stretch;
        _viewportCanvas.VerticalAlignment = VerticalAlignment.Stretch;
        vpGrid.Children.Add(_viewportCanvas);

        // Telemetry Overlay in bottom-left of viewport
        var telemetryCard = new Border
        {
            Background = ColorBrush(0xCC111116),
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusCard,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var telStack = new StackPanel();
        _telemetryActionText.Text = "Action: computer_click";
        _telemetryActionText.FontSize = 11;
        _telemetryActionText.FontWeight = FontWeights.SemiBold;
        _telemetryActionText.Foreground = InbriskTheme.TextPrimary;
        telStack.Children.Add(_telemetryActionText);

        _telemetryCoordsText.Text = "Coordinates: (X: 480, Y: 320)";
        _telemetryCoordsText.FontSize = 10.5;
        _telemetryCoordsText.Foreground = InbriskTheme.TextSecondary;
        telStack.Children.Add(_telemetryCoordsText);

        _telemetryLatencyText.Text = "UIA Latency: 3.2ms • Verification: Verified";
        _telemetryLatencyText.FontSize = 10;
        _telemetryLatencyText.Foreground = InbriskTheme.StateSuccess;
        telStack.Children.Add(_telemetryLatencyText);
        telemetryCard.Child = telStack;
        vpGrid.Children.Add(telemetryCard);

        _viewportSurface.Child = vpGrid;
        _viewportSurface.SizeChanged += (_, _) => RedrawViewportCanvas();
        Grid.SetRow(_viewportSurface, 1);
        leftPanel.Children.Add(_viewportSurface);

        // Live Action Caption
        var captionBorder = new Border
        {
            Background = InbriskTheme.SurfaceElevated,
            BorderBrush = InbriskTheme.BorderEmber,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusCard,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 10, 0, 0)
        };
        var capStack = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = InbriskTheme.AccentGlow,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        capStack.Children.Add(dot);

        _liveActionCaption.Text = "Agent targeting Microsoft Word: Ready for automation";
        _liveActionCaption.FontSize = 12;
        _liveActionCaption.Foreground = InbriskTheme.TextPrimary;
        capStack.Children.Add(_liveActionCaption);
        captionBorder.Child = capStack;
        Grid.SetRow(captionBorder, 2);
        leftPanel.Children.Add(captionBorder);

        Grid.SetColumn(leftPanel, 0);
        grid.Children.Add(leftPanel);

        // Right: Compact Activity Panel
        var rightPanel = new Border
        {
            Background = InbriskTheme.Surface,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusPanel,
            Padding = new Thickness(14, 12, 14, 12)
        };
        var rightGrid = new Grid();
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var streamHeader = new TextBlock
        {
            Text = "Live Activity Stream",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = InbriskTheme.TextPrimary,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(streamHeader, 0);
        rightGrid.Children.Add(streamHeader);

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _compactActivityList.Children.Clear();
        RenderCompactActivityList();
        scroller.Content = _compactActivityList;
        Grid.SetRow(scroller, 1);
        rightGrid.Children.Add(scroller);

        rightPanel.Child = rightGrid;
        Grid.SetColumn(rightPanel, 2);
        grid.Children.Add(rightPanel);

        return grid;
    }

    private void RedrawViewportCanvas()
    {
        _viewportCanvas.Children.Clear();
        var w = _viewportCanvas.ActualWidth;
        var h = _viewportCanvas.ActualHeight;
        if (w < 40 || h < 40) return;

        // Visual simulated window frame inside canvas
        var margin = 20;
        var frameW = Math.Max(w - margin * 2, 100);
        var frameH = Math.Max(h - margin * 2, 80);

        var frame = new Rectangle
        {
            Width = frameW,
            Height = frameH,
            Stroke = _isActive ? InbriskTheme.Accent : InbriskTheme.BorderMedium,
            StrokeThickness = _isActive ? 1.8 : 1.0,
            Fill = InbriskTheme.SurfaceCard,
            RadiusX = 8,
            RadiusY = 8
        };
        Canvas.SetLeft(frame, margin);
        Canvas.SetTop(frame, margin);
        _viewportCanvas.Children.Add(frame);

        // Frame title bar simulation
        var titleBar = new Rectangle
        {
            Width = frameW,
            Height = 24,
            Fill = InbriskTheme.SurfaceElevated,
            RadiusX = 8,
            RadiusY = 8
        };
        Canvas.SetLeft(titleBar, margin);
        Canvas.SetTop(titleBar, margin);
        _viewportCanvas.Children.Add(titleBar);

        var titleSim = new TextBlock
        {
            Text = _activeTargetTitle,
            FontSize = 10,
            Foreground = InbriskTheme.TextSecondary,
            Margin = new Thickness(8, 5, 0, 0)
        };
        Canvas.SetLeft(titleSim, margin);
        Canvas.SetTop(titleSim, margin);
        _viewportCanvas.Children.Add(titleSim);

        // Target crosshair / action point
        if (_isActive)
        {
            var cx = margin + frameW * 0.45;
            var cy = margin + frameH * 0.52;

            var pulseRing = new Ellipse
            {
                Width = 26,
                Height = 26,
                Stroke = InbriskTheme.AccentGlow,
                StrokeThickness = 1.5,
                Fill = InbriskTheme.AccentSoft
            };
            Canvas.SetLeft(pulseRing, cx - 13);
            Canvas.SetTop(pulseRing, cy - 13);
            _viewportCanvas.Children.Add(pulseRing);

            var centerDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = InbriskTheme.AccentGlow
            };
            Canvas.SetLeft(centerDot, cx - 3);
            Canvas.SetTop(centerDot, cy - 3);
            _viewportCanvas.Children.Add(centerDot);
        }
    }

    // -------------------------------------------------------------
    // Section E: Activity Panel / Timeline
    // -------------------------------------------------------------
    private UIElement BuildActivityTimelinePane()
    {
        var panel = new Border
        {
            Background = InbriskTheme.Surface,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusPanel,
            Padding = new Thickness(18, 14, 18, 14)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var title = new TextBlock
        {
            Text = "Runtime Activity Stream",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = InbriskTheme.TextPrimary
        };
        headerRow.Children.Add(title);

        var countBadge = new Border
        {
            Background = InbriskTheme.SurfaceElevated,
            CornerRadius = InbriskTheme.RadiusTag,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            Child = new TextBlock
            {
                Text = $"{_activityRecords.Count} recorded actions",
                FontSize = 11,
                Foreground = InbriskTheme.TextSecondary
            }
        };
        headerRow.Children.Add(countBadge);
        Grid.SetRow(headerRow, 0);
        grid.Children.Add(headerRow);

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _activityTimelineList.Children.Clear();
        foreach (var rec in _activityRecords)
        {
            _activityTimelineList.Children.Add(CreateActivityRow(rec));
        }
        scroller.Content = _activityTimelineList;
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);

        panel.Child = grid;
        return panel;
    }

    private UIElement CreateActivityRow((DateTime Time, string Action, string Target, string Details, string Outcome) rec)
    {
        var row = new Border
        {
            Background = InbriskTheme.SurfaceElevated,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusCard,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Time
        var time = new TextBlock
        {
            Text = rec.Time.ToString("HH:mm:ss"),
            FontSize = 11,
            Foreground = InbriskTheme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 0);
        grid.Children.Add(time);

        // Action badge
        var actBadge = new Border
        {
            Background = InbriskTheme.AccentSoft,
            CornerRadius = InbriskTheme.RadiusTag,
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = rec.Action,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = InbriskTheme.TextEmber
            }
        };
        Grid.SetColumn(actBadge, 1);
        grid.Children.Add(actBadge);

        // Details
        var detailsStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var targetText = new TextBlock
        {
            Text = rec.Target,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = InbriskTheme.TextPrimary
        };
        var subText = new TextBlock
        {
            Text = rec.Details,
            FontSize = 10.5,
            Foreground = InbriskTheme.TextSecondary
        };
        detailsStack.Children.Add(targetText);
        detailsStack.Children.Add(subText);
        Grid.SetColumn(detailsStack, 2);
        grid.Children.Add(detailsStack);

        // Outcome
        var outcome = new TextBlock
        {
            Text = rec.Outcome,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = rec.Outcome == "VERIFIED" || rec.Outcome == "OK" ? InbriskTheme.StateSuccess : InbriskTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(outcome, 3);
        grid.Children.Add(outcome);

        row.Child = grid;
        return row;
    }

    private void RenderCompactActivityList()
    {
        _compactActivityList.Children.Clear();
        foreach (var rec in _activityRecords.Take(6))
        {
            var item = new Border
            {
                Background = InbriskTheme.SurfaceElevated,
                CornerRadius = InbriskTheme.RadiusTag,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };
            var st = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(new TextBlock
            {
                Text = rec.Action,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = InbriskTheme.TextEmber,
                Margin = new Thickness(0, 0, 6, 0)
            });
            top.Children.Add(new TextBlock
            {
                Text = rec.Time.ToString("HH:mm:ss"),
                FontSize = 9.5,
                Foreground = InbriskTheme.TextMuted
            });
            st.Children.Add(top);
            st.Children.Add(new TextBlock
            {
                Text = rec.Details,
                FontSize = 10.5,
                Foreground = InbriskTheme.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            item.Child = st;
            _compactActivityList.Children.Add(item);
        }
    }

    // -------------------------------------------------------------
    // Tab: Permissions & Safety
    // -------------------------------------------------------------
    private UIElement BuildPermissionsPane()
    {
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var p = new StackPanel { Margin = new Thickness(4) };

        p.Children.Add(new TextBlock
        {
            Text = "Runtime Safety & Guardrails",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = InbriskTheme.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4)
        });
        p.Children.Add(new TextBlock
        {
            Text = "Authoritative protection rules enforced deterministically across all MCP tool calls.",
            FontSize = 11.5,
            Foreground = InbriskTheme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 14)
        });

        // Card 1: Protected Processes
        var procCard = new Border
        {
            Background = InbriskTheme.Surface,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusPanel,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var stProc = new StackPanel();
        stProc.Children.Add(new TextBlock
        {
            Text = "PROTECTED PROCESSES (STRICTLY FORBIDDEN TO CLOSE OR TERMINATE)",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = InbriskTheme.TextEmber,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var procs = new[]
        {
            ("Antigravity.exe", "Google DeepMind AI IDE runtime"),
            ("Hermes.exe", "Lead Architect Agent Runtime"),
            ("Code.exe", "VS Code Development Environment"),
            ("devenv.exe", "Visual Studio IDE Host"),
            ("pwsh.exe / cmd.exe", "Interactive Shell & Terminal Hosts")
        };
        foreach (var (proc, desc) in procs)
        {
            var r = new Border
            {
                Background = InbriskTheme.SurfaceElevated,
                CornerRadius = InbriskTheme.RadiusTag,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 3)
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var p1 = new TextBlock { Text = proc, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = InbriskTheme.TextPrimary };
            var p2 = new TextBlock { Text = desc, FontSize = 10.5, Foreground = InbriskTheme.TextSecondary };
            var p3 = new TextBlock { Text = "IMMUTABLE LOCK", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = InbriskTheme.StateEmergency };
            Grid.SetColumn(p1, 0); g.Children.Add(p1);
            Grid.SetColumn(p2, 1); g.Children.Add(p2);
            Grid.SetColumn(p3, 2); g.Children.Add(p3);
            r.Child = g;
            stProc.Children.Add(r);
        }
        procCard.Child = stProc;
        p.Children.Add(procCard);

        // Card 2: Conservative Lifecycle Disposition
        var lifeCard = new Border
        {
            Background = InbriskTheme.Surface,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusPanel,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var stLife = new StackPanel();
        stLife.Children.Add(new TextBlock
        {
            Text = "CONSERVATIVE LIFECYCLE & CLEANUP DISPOSITION",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = InbriskTheme.TextEmber,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var policies = new[]
        {
            ("Useful Artifacts (Notepad, Word, Explorer)", "Keep Open by Default", "Opened windows containing user work are preserved."),
            ("Ephemeral Test Windows", "Close on Task End", "Disposable helper windows closed safely via WM_CLOSE."),
            ("User-Owned Applications", "Strict User-Only Authority", "Agent cannot close without explicit user command.")
        };
        foreach (var (name, disp, exp) in policies)
        {
            var r = new Border
            {
                Background = InbriskTheme.SurfaceElevated,
                CornerRadius = InbriskTheme.RadiusTag,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var st = new StackPanel();
            var rowTop = new StackPanel { Orientation = Orientation.Horizontal };
            rowTop.Children.Add(new TextBlock { Text = name, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = InbriskTheme.TextPrimary });
            rowTop.Children.Add(new TextBlock { Text = $" → {disp}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = InbriskTheme.StateSuccess });
            st.Children.Add(rowTop);
            st.Children.Add(new TextBlock { Text = exp, FontSize = 10.5, Foreground = InbriskTheme.TextSecondary, Margin = new Thickness(0, 2, 0, 0) });
            r.Child = st;
            stLife.Children.Add(r);
        }
        lifeCard.Child = stLife;
        p.Children.Add(lifeCard);

        scroller.Content = p;
        return scroller;
    }

    // -------------------------------------------------------------
    // Tab: Settings
    // -------------------------------------------------------------
    private UIElement BuildSettingsPane()
    {
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var p = new StackPanel { Margin = new Thickness(4) };

        p.Children.Add(new TextBlock
        {
            Text = "Runtime Configuration",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = InbriskTheme.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4)
        });
        p.Children.Add(new TextBlock
        {
            Text = "Configure safety hotkey chords, visual HUD pill behavior, and perimeter options.",
            FontSize = 11.5,
            Foreground = InbriskTheme.TextSecondary,
            Margin = new Thickness(0, 0, 0, 14)
        });

        // Hotkeys Card
        var hotkeyCard = new Border
        {
            Background = InbriskTheme.Surface,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusPanel,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var hkStack = new StackPanel();
        hkStack.Children.Add(new TextBlock
        {
            Text = "EMERGENCY CHORD BINDINGS",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = InbriskTheme.TextEmber,
            Margin = new Thickness(0, 0, 0, 8)
        });

        hkStack.Children.Add(CreateConfigRow("Panic Hotkey (Instant Freeze)", _settings.PanicHotkey ?? "Ctrl+Alt+Pause"));
        hkStack.Children.Add(CreateConfigRow("Resume Hotkey (Clear Freeze)", _settings.ResumeHotkey ?? "Ctrl+Alt+Shift+Pause"));
        hotkeyCard.Child = hkStack;
        p.Children.Add(hotkeyCard);

        // Visual options
        var visualCard = new Border
        {
            Background = InbriskTheme.Surface,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = InbriskTheme.RadiusPanel,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var visStack = new StackPanel();
        visStack.Children.Add(new TextBlock
        {
            Text = "ACTIVITY-ONLY VISUAL SURFACES",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = InbriskTheme.TextEmber,
            Margin = new Thickness(0, 0, 0, 8)
        });

        visStack.Children.Add(CreateConfigRow("Taskbar Floating Pill HUD", "Activity-Driven (Visible only during active work)"));

        var perimCheck = new CheckBox
        {
            IsChecked = _settings.PerimeterEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        perimCheck.Checked += (_, _) => { _settings.PerimeterEnabled = true; _settings.Save(); };
        perimCheck.Unchecked += (_, _) => { _settings.PerimeterEnabled = false; _settings.Save(); };
        visStack.Children.Add(CreateConfigRow("Screen Perimeter Wall", perimCheck));

        visStack.Children.Add(CreateColorRow("Working Border Color",
            _settings.IdleColor ?? "#B7FF3C",
            ["#B7FF3C", "#40C4FF", "#FF80AB", "#FFD740", "#B388FF"],
            hex => { _settings.IdleColor = hex; _settings.Save(); }));
        visStack.Children.Add(CreateColorRow("Emergency Border Color",
            _settings.EmergencyColor ?? "#FF3B3B",
            ["#FF3B3B", "#FF9100", "#FF1744", "#FFFF00"],
            hex => { _settings.EmergencyColor = hex; _settings.Save(); }));

        visStack.Children.Add(CreateConfigRow("Controlled Window Highlight", "Transient (Disappears when tool completes)"));
        visualCard.Child = visStack;
        p.Children.Add(visualCard);

        scroller.Content = p;
        return scroller;
    }

    private UIElement CreateConfigRow(string label, string value)
    {
        var r = new Border
        {
            Background = InbriskTheme.SurfaceElevated,
            CornerRadius = InbriskTheme.RadiusTag,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 4)
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = InbriskTheme.TextPrimary };
        var v = new TextBlock { Text = value, FontSize = 11, Foreground = InbriskTheme.TextSecondary };
        Grid.SetColumn(l, 0); g.Children.Add(l);
        Grid.SetColumn(v, 1); g.Children.Add(v);
        r.Child = g;
        return r;
    }

    private UIElement CreateConfigRow(string label, UIElement valueControl)
    {
        var r = new Border
        {
            Background = InbriskTheme.SurfaceElevated,
            CornerRadius = InbriskTheme.RadiusTag,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 4)
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = InbriskTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(l, 0); g.Children.Add(l);
        Grid.SetColumn(valueControl, 1); g.Children.Add(valueControl);
        r.Child = g;
        return r;
    }

    private UIElement CreateColorRow(string label, string initial, string[] presets, Action<string> onSave)
    {
        var preview = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = InbriskTheme.BorderMedium,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var hex = new TextBox
        {
            Text = initial,
            Width = 76,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Background = InbriskTheme.SurfaceCard,
            Foreground = InbriskTheme.TextPrimary,
            BorderBrush = InbriskTheme.BorderSubtle,
            Padding = new Thickness(5, 2, 5, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        void Refresh()
        {
            var c = UserSettings.ParseColor(hex.Text);
            preview.Background = c is { } rgb
                ? new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B))
                : Brushes.Transparent;
        }
        Refresh();
        hex.TextChanged += (_, _) =>
        {
            Refresh();
            if (UserSettings.ParseColor(hex.Text) is { }) onSave(hex.Text.Trim());
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(preview);
        row.Children.Add(hex);
        foreach (var pre in presets)
        {
            var c = UserSettings.ParseColor(pre)!.Value;
            var swatch = new Button
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(5, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            var captured = pre;
            swatch.Click += (_, _) => hex.Text = captured;
            row.Children.Add(swatch);
        }
        return CreateConfigRow(label, row);
    }

    // -------------------------------------------------------------
    // Section F: Footer / Session Status
    // -------------------------------------------------------------
    private UIElement BuildFooter()
    {
        var footerBorder = new Border
        {
            Background = InbriskTheme.Void,
            BorderBrush = InbriskTheme.BorderSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 16, 16),
            Padding = new Thickness(20, 0, 20, 0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left items
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _footerMachineText.Text = $"Host: {Environment.MachineName} (Windows 11)";
        _footerMachineText.FontSize = 10;
        _footerMachineText.Foreground = InbriskTheme.TextMuted;
        left.Children.Add(_footerMachineText);

        left.Children.Add(new TextBlock { Text = "  •  ", FontSize = 10, Foreground = InbriskTheme.BorderMedium });

        _footerSessionText.Text = "Session: sess-inbrisk";
        _footerSessionText.FontSize = 10;
        _footerSessionText.Foreground = InbriskTheme.TextMuted;
        left.Children.Add(_footerSessionText);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Right items
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _footerRuntimeText.Text = "MCP Engine: Stdio Server (net8.0-windows)";
        _footerRuntimeText.FontSize = 10;
        _footerRuntimeText.Foreground = InbriskTheme.TextMuted;
        right.Children.Add(_footerRuntimeText);

        right.Children.Add(new TextBlock { Text = "  •  ", FontSize = 10, Foreground = InbriskTheme.BorderMedium });

        _footerReadinessText.Text = "UI Automation: UIA ReadPool Active | WGC Ready";
        _footerReadinessText.FontSize = 10;
        _footerReadinessText.Foreground = InbriskTheme.StateSuccess;
        right.Children.Add(_footerReadinessText);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        footerBorder.Child = grid;
        return footerBorder;
    }

    // -------------------------------------------------------------
    // State Polling & Reactive Updates
    // -------------------------------------------------------------
    private void PollRuntimeState()
    {
        // 1. Check emergency state
        _isEmergency = EmergencyGate.IsStopped;

        // 2. Query active foreground window
        try
        {
            var fg = _windowService.GetForegroundWindow();
            if (fg != null && fg.Hwnd != 0)
            {
                _activeTargetTitle = !string.IsNullOrWhiteSpace(fg.Title) ? fg.Title : (fg.ProcessName ?? "Active Window");
                _activeTargetProcess = fg.ProcessName ?? "explorer.exe";
                _activeTargetPid = fg.Pid;
                _activeTargetHwnd = fg.Hwnd;

                _targetTitleText.Text = _activeTargetTitle;
                _targetProcessText.Text = $"{_activeTargetProcess} (PID: {_activeTargetPid})";
                _targetDetailsText.Text = $"HWND: 0x{_activeTargetHwnd:X8} • Bounds: {fg.Bounds.Width}x{fg.Bounds.Height}";
            }
        }
        catch { }

        // 3. Check activity status
        try
        {
            var hudText = ActivityHudService.ActiveHudText;
            if (!string.IsNullOrEmpty(hudText) && !_isEmergency)
            {
                _isActive = true;
                _lastOperation = hudText;
            }
            else
            {
                _isActive = false;
            }
        }
        catch { }

        UpdateStateDisplay();
    }

    private void UpdateStateDisplay()
    {
        if (_isEmergency)
        {
            _stateBadgeBorder.Background = InbriskTheme.StateEmergencyGlow;
            _stateBadgeDot.Fill = InbriskTheme.StateEmergency;
            _stateBadgeText.Text = "EMERGENCY STOPPED";
            _stateBadgeText.Foreground = InbriskTheme.StateEmergency;
            _stateMessageText.Text = "Acil Durdurma aktif — tüm fare ve klavye işlemleri kilitlendi.";
            _emergencyBtn.Content = "⏻ Agent: Kapalı";
            _emergencyBtn.Background = InbriskTheme.StateEmergency;
            _emergencyBtn.BorderBrush = InbriskTheme.StateEmergencyGlow;
            _liveActionCaption.Text = "Tüm otomasyon durduruldu — acil fren devrede";
        }
        else if (_isActive)
        {
            _stateBadgeBorder.Background = InbriskTheme.StateActiveGlow;
            _stateBadgeDot.Fill = InbriskTheme.StateActive;
            _stateBadgeText.Text = "AGENT ACTIVE";
            _stateBadgeText.Foreground = InbriskTheme.StateActive;
            _stateMessageText.Text = $"İşlem yapılıyor: {_lastOperation}";
            _emergencyBtn.Content = "⏻ Agent: Açık";
            _emergencyBtn.Background = InbriskTheme.StateSuccess;
            _emergencyBtn.BorderBrush = InbriskTheme.StateSuccessGlow;
            _liveActionCaption.Text = $"Agent {_activeTargetProcess} üzerinde işlem yapıyor: {_lastOperation}";
        }
        else
        {
            _stateBadgeBorder.Background = InbriskTheme.StateIdleSubtle;
            _stateBadgeDot.Fill = InbriskTheme.StateIdle;
            _stateBadgeText.Text = "IDLE / HAZIR";
            _stateBadgeText.Foreground = InbriskTheme.TextSecondary;
            _stateMessageText.Text = "Runtime hazır • Yapay zeka ajanından komut bekleniyor.";
            _emergencyBtn.Content = "⏻ Agent: Açık";
            _emergencyBtn.Background = InbriskTheme.StateSuccess;
            _emergencyBtn.BorderBrush = InbriskTheme.StateSuccessGlow;
            _liveActionCaption.Text = $"{_activeTargetProcess} hedefleniyor: Yeni işlem bekleniyor";
        }

        RedrawViewportCanvas();
    }

    private void TriggerEmergencyStop()
    {
        if (_isEmergency)
        {
            try { File.Delete(EmergencyControl.Process.StopMarkerPath); } catch { }
            try { File.Delete(InstallLayout.EmergencyMarkerPath); } catch { }
            EmergencyControl.Process.TriggerLocalResume("control window");
            _isEmergency = false;
        }
        else
        {
            try { File.WriteAllText(EmergencyControl.Process.StopMarkerPath, DateTimeOffset.UtcNow.ToString("O")); } catch { }
            try { File.WriteAllText(InstallLayout.EmergencyMarkerPath, DateTimeOffset.UtcNow.ToString("O")); } catch { }
            EmergencyControl.Process.TriggerLocalPanic("control window");
            _isEmergency = true;
        }
        UpdateStateDisplay();
    }

    private void SeedSampleActivity()
    {
        var now = DateTime.Now;
        _activityRecords.Add((now.AddSeconds(-12), "LAUNCH", "Microsoft Word", "Launched via ApplicationFrameHost", "OK"));
        _activityRecords.Add((now.AddSeconds(-9), "FOCUS", "Microsoft Word", "HWND: 0x001402F0 brought to foreground", "OK"));
        _activityRecords.Add((now.AddSeconds(-7), "FIND", "Microsoft Word", "Located DocumentBody paragraph area", "VERIFIED"));
        _activityRecords.Add((now.AddSeconds(-5), "CLICK", "Microsoft Word", "Clicked Body at (X: 480, Y: 320)", "OK"));
        _activityRecords.Add((now.AddSeconds(-3), "TYPE", "Microsoft Word", "Typed 'Inbrisk Runtime Architecture'", "VERIFIED"));
        _activityRecords.Add((now.AddSeconds(-1), "SCROLL", "Microsoft Word", "Scrolled document (delta -120)", "OK"));
    }

    private static SolidColorBrush ColorBrush(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16),
            (byte)(argb >> 8), (byte)argb));
}
