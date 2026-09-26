using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Hud;

/// <summary>
/// Activity-state → HUD transition policy (production wiring lives in
/// InbriskRuntime; extracted so the policy is testable without creating
/// the HUD window — an unstarted ActivityHudService still runs its full
/// state machine and renders nothing).
/// </summary>
public static class HudActivityPolicy
{
    /// <summary>
    /// Emergency is entered ONLY on EmergencyStopped and left on ANY
    /// non-emergency state. Resume while a client is attached lands on
    /// ConnectedIdle/Active — not Disconnected — so gating the clear on
    /// Disconnected left "Inbrisk durduruldu" on screen forever. The
    /// clear is a clean transition: no success bloom.
    /// </summary>
    public static void Apply(ActivityHudService hud, IndicatorState state)
    {
        if (state == IndicatorState.EmergencyStopped)
        {
            hud.SetEmergency(true);
            return;
        }
        hud.SetEmergency(false);
        if (state == IndicatorState.ConnectedIdle && hud.State == HudState.Working)
            hud.SetSuccess();
    }
}
