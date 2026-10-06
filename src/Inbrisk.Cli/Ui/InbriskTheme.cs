using System.Windows;
using System.Windows.Media;

namespace Inbrisk.Cli.Ui;

/// <summary>
/// Design tokens and palette for the Inbrisk Desktop Control Runtime.
/// Windows 11 Obsidian Glass aesthetic with subtle warm ember / orange-red brand accents.
/// </summary>
public static class InbriskTheme
{
    // -------------------------------------------------------------
    // Surface & Glass Layers (Obsidian Dark Glass)
    // -------------------------------------------------------------
    public static readonly Color VoidColor = Color.FromRgb(0x10, 0x10, 0x14);
    public static readonly Color BackgroundColor = Color.FromRgb(0x13, 0x13, 0x18);
    public static readonly Color SurfaceColor = Color.FromRgb(0x1A, 0x1A, 0x22);
    public static readonly Color SurfaceElevatedColor = Color.FromRgb(0x23, 0x23, 0x2E);
    public static readonly Color SurfaceHoverColor = Color.FromRgb(0x2C, 0x2C, 0x3A);
    public static readonly Color SurfaceCardColor = Color.FromRgb(0x1E, 0x1E, 0x28);

    public static readonly SolidColorBrush Void = B(0xFF101014);
    public static readonly SolidColorBrush Background = B(0xFF131318);
    public static readonly SolidColorBrush Surface = B(0xFF1A1A22);
    public static readonly SolidColorBrush SurfaceElevated = B(0xFF23232E);
    public static readonly SolidColorBrush SurfaceHover = B(0xFF2C2C3A);
    public static readonly SolidColorBrush SurfaceCard = B(0xFF1E1E28);

    // -------------------------------------------------------------
    // Borders & Hairlines
    // -------------------------------------------------------------
    public static readonly SolidColorBrush BorderSubtle = B(0x1AFFFFFF); // 10% white
    public static readonly SolidColorBrush BorderMedium = B(0x2BFFFFFF); // 17% white
    public static readonly SolidColorBrush BorderHighlight = B(0x40FFFFFF);
    public static readonly SolidColorBrush BorderEmber = B(0x55E54B35); // 33% ember

    // -------------------------------------------------------------
    // Brand Accent: Warm Ember / Orange-Red
    // -------------------------------------------------------------
    public static readonly Color AccentColor = Color.FromRgb(0xE5, 0x4B, 0x35);
    public static readonly Color AccentGlowColor = Color.FromRgb(0xFF, 0x57, 0x33);
    public static readonly Color AccentSubtleColor = Color.FromArgb(0x35, 0xE5, 0x4B, 0x35);

    public static readonly SolidColorBrush Accent = B(0xFFE54B35);
    public static readonly SolidColorBrush AccentGlow = B(0xFFFF5733);
    public static readonly SolidColorBrush AccentSoft = B(0x25E54B35);
    public static readonly SolidColorBrush AccentBadge = B(0x40E54B35);

    // -------------------------------------------------------------
    // State Semantics
    // -------------------------------------------------------------
    // Active (Ember Warm Red pulse)
    public static readonly SolidColorBrush StateActive = B(0xFFFF5733);
    public static readonly SolidColorBrush StateActiveGlow = B(0x35FF5733);

    // Idle (Calm Muted Slate/Zinc)
    public static readonly SolidColorBrush StateIdle = B(0xFF71717A);
    public static readonly SolidColorBrush StateIdleSubtle = B(0x2071717A);

    // Paused (Amber)
    public static readonly SolidColorBrush StatePaused = B(0xFFF59E0B);
    public static readonly SolidColorBrush StatePausedGlow = B(0x35F59E0B);

    // Emergency (Urgent Crimson Alert)
    public static readonly SolidColorBrush StateEmergency = B(0xFFEF4444);
    public static readonly SolidColorBrush StateEmergencyGlow = B(0x45EF4444);

    // Success / Settle (Emerald Green)
    public static readonly SolidColorBrush StateSuccess = B(0xFF10B981);
    public static readonly SolidColorBrush StateSuccessGlow = B(0x3010B981);

    // -------------------------------------------------------------
    // Typography Brushes
    // -------------------------------------------------------------
    public static readonly SolidColorBrush TextPrimary = B(0xFFF4F4F6);
    public static readonly SolidColorBrush TextSecondary = B(0xFFA1A1AA);
    public static readonly SolidColorBrush TextMuted = B(0xFF71717A);
    public static readonly SolidColorBrush TextEmber = B(0xFFFF7A60);

    // -------------------------------------------------------------
    // Corner Radii
    // -------------------------------------------------------------
    public static readonly CornerRadius RadiusWindow = new(16);
    public static readonly CornerRadius RadiusPanel = new(12);
    public static readonly CornerRadius RadiusCard = new(10);
    public static readonly CornerRadius RadiusButton = new(8);
    public static readonly CornerRadius RadiusPill = new(20);
    public static readonly CornerRadius RadiusTag = new(5);

    private static SolidColorBrush B(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16),
            (byte)(argb >> 8), (byte)argb));
}
