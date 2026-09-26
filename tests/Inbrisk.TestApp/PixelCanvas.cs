using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Inbrisk.TestApp;

/// <summary>
/// Pixel-only test surface: everything inside is drawn via OnRender, so UIA
/// sees only the host element (a Custom pane) — no children. Contains a fixed
/// magenta target square (vision/clickable), rendered text (OCR-visible),
/// a moving ball (change signal) and a drawn state label.
/// </summary>
public sealed class PixelCanvas : FrameworkElement
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private double _ballX;
    private bool _ballRight = true;
    private int _hits;
    private bool _animating;

    /// <summary>The magenta click target, in canvas-local coordinates.</summary>
    public static readonly Rect TargetRect = new(260, 30, 64, 64);
    /// <summary>Ball travel lane.</summary>
    private static readonly Rect Lane = new(16, 110, 368, 28);

    public bool Animating => _animating;

    public PixelCanvas()
    {
        Width = 400; Height = 150;
        SnapsToDevicePixels = true;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "PixelCanvas");
        System.Windows.Automation.AutomationProperties.SetName(this, "Pixel canvas");
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33), // ~30fps
        };
        _timer.Tick += (_, _) =>
        {
            _ballX += _ballRight ? 6 : -6;
            if (_ballX >= Lane.Width - 28) _ballRight = false;
            if (_ballX <= 0) _ballRight = true;
            InvalidateVisual();
        };
    }

    public void StartAnimation() { _animating = true; _timer.Start(); InvalidateVisual(); }
    public void StopAnimation() { _animating = false; _timer.Stop(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)), null,
            new Rect(0, 0, Width, Height));

        // rendered text — OCR can read it, UIA cannot
        var text = new FormattedText("PIXEL TARGET",
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 20, Brushes.White, 1.0);
        dc.DrawText(text, new Point(16, 16));

        var state = new FormattedText($"STATE:{(_animating ? "ANIM" : "IDLE")} HITS:{_hits}",
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.LightGray, 1.0);
        dc.DrawText(state, new Point(16, 48));

        // magenta click target
        dc.DrawRectangle(Brushes.Magenta, null, TargetRect);
        var tg = new FormattedText("TGT",
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 14, Brushes.Black, 1.0);
        dc.DrawText(tg, new Point(TargetRect.X + 14, TargetRect.Y + 24));

        // moving ball — large enough to clear the differ's cell threshold
        dc.DrawEllipse(Brushes.OrangeRed, null,
            new Point(Lane.X + _ballX + 14, Lane.Y + 14), 14, 14);
        dc.DrawRectangle(null, new Pen(Brushes.DimGray, 1), Lane);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (TargetRect.Contains(p))
        {
            _hits++;
            MainWindow.WriteState($"canvas:hit:{_hits}");
            InvalidateVisual();
        }
        else
        {
            MainWindow.WriteState($"canvas:miss:{(int)p.X},{(int)p.Y}");
        }
        base.OnMouseLeftButtonDown(e);
    }
}
