using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Indicator;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Screen-control indicator: activity-lease state machine (pure), glow
/// metrics + breathing wave (pure), and live overlay windows on the real
/// desktop.
/// </summary>
[Collection("desktop")]
public class IndicatorTests
{
    private readonly DesktopFixture _fx;
    public IndicatorTests(DesktopFixture fx) => _fx = fx;

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private const int ExStyle = -20;
    private const int WsExTransparent = 0x20, WsExTopmost = 0x08,
        WsExToolwindow = 0x80, WsExNoactivate = 0x08000000, WsExLayered = 0x80000;

    // --------------------------------------------------------------
    // Activity lease state machine
    // --------------------------------------------------------------

    [Fact]
    public void Leases_AreRefCounted()
    {
        var a = new ComputerControlActivityService(idleGraceMs: 60);
        a.SetConnected(true);
        Assert.Equal(IndicatorState.ConnectedIdle, a.State);

        var l1 = a.BeginActivity();
        var l2 = a.BeginActivity();
        Assert.Equal(IndicatorState.Active, a.State);

        l1.Dispose(); // one lease ends, another still active
        Thread.Sleep(200);
        Assert.Equal(IndicatorState.Active, a.State);

        l2.Dispose(); // last lease → grace → idle
        Assert.True(SpinWait.SpinUntil(() => a.State == IndicatorState.ConnectedIdle, 1500),
            $"state={a.State} after grace");
        a.Dispose();
    }

    [Fact]
    public void RapidActions_DoNotFlicker()
    {
        var a = new ComputerControlActivityService(idleGraceMs: 500);
        var seen = new List<IndicatorState>();
        a.StateChanged += s => { lock (seen) seen.Add(s); };
        a.SetConnected(true);

        // fast back-to-back operations — like find→click→type in a run
        for (var i = 0; i < 4; i++)
        {
            using (a.BeginActivity()) Thread.Sleep(80);
            Thread.Sleep(80); // gap between actions stays inside grace
        }
        lock (seen)
            Assert.DoesNotContain(IndicatorState.ConnectedIdle,
                seen.Skip(1)); // after the initial connect transition
        a.Dispose();
    }

    [Fact]
    public void Exception_StillReleasesLease()
    {
        var a = new ComputerControlActivityService(idleGraceMs: 50);
        a.SetConnected(true);
        try { using (a.BeginActivity()) throw new InvalidOperationException(); }
        catch (InvalidOperationException) { }
        Assert.Equal(0, a.ActiveLeases);
        Assert.True(SpinWait.SpinUntil(() => a.State == IndicatorState.ConnectedIdle, 1000));
        a.Dispose();
    }

    [Fact]
    public void Emergency_OverridesActive_AndResumeRestores()
    {
        var a = new ComputerControlActivityService(idleGraceMs: 100);
        a.SetConnected(true);
        var l = a.BeginActivity();
        Assert.Equal(IndicatorState.Active, a.State);

        a.SetEmergency(true);
        Assert.Equal(IndicatorState.EmergencyStopped, a.State);

        a.SetEmergency(false);
        Assert.Equal(IndicatorState.Active, a.State); // lease still held
        l.Dispose();
        a.Dispose();
    }

    [Fact]
    public void Disconnected_HasNoIndicator()
    {
        var a = new ComputerControlActivityService();
        using var l = a.BeginActivity();
        Assert.Equal(IndicatorState.Disconnected, a.State);
        a.SetConnected(true);
        Assert.Equal(IndicatorState.Active, a.State);
        a.SetConnected(false);
        Assert.Equal(IndicatorState.Disconnected, a.State);
        a.Dispose();
    }

    [Fact]
    public void Clients_AreRefCounted_MultiSessionSafe()
    {
        var a = new ComputerControlActivityService(idleGraceMs: 60);
        Assert.Equal(IndicatorState.Disconnected, a.State); // 0 clients

        var c1 = a.AttachClient();
        var c2 = a.AttachClient();
        Assert.Equal(2, a.ActiveClients);
        Assert.Equal(IndicatorState.ConnectedIdle, a.State);

        c1.Dispose(); // one session gone — the other still owns the smoke
        Assert.Equal(1, a.ActiveClients);
        Assert.Equal(IndicatorState.ConnectedIdle, a.State);

        c1.Dispose(); // idempotent — must NOT double-decrement
        Assert.Equal(1, a.ActiveClients);
        Assert.Equal(IndicatorState.ConnectedIdle, a.State);

        c2.Dispose();
        Assert.Equal(0, a.ActiveClients);
        Assert.Equal(IndicatorState.Disconnected, a.State);
        a.Dispose();
    }

    [Fact]
    public void Disconnect_DuringActivity_DropsStraightToDisconnected()
    {
        var a = new ComputerControlActivityService(idleGraceMs: 500);
        var c = a.AttachClient();
        var l = a.BeginActivity();
        Assert.Equal(IndicatorState.Active, a.State);

        c.Dispose(); // client gone mid-activity — outstanding leases are
        // irrelevant: no client means no indicator
        Assert.Equal(IndicatorState.Disconnected, a.State);
        l.Dispose();
        a.Dispose();
    }

    [Fact]
    public void Emergency_WithoutClient_RendersNothing()
    {
        var a = new ComputerControlActivityService();
        a.SetEmergency(true); // panic persisted with no client attached —
        // the overlay must stay off: Disconnected wins over EmergencyStopped
        Assert.Equal(IndicatorState.Disconnected, a.State);
        var c = a.AttachClient();
        Assert.Equal(IndicatorState.EmergencyStopped, a.State); // red once a client attaches
        c.Dispose();
        a.Dispose();
    }

    [Fact]
    public void Overlay_Disconnect_FadesOut_ThenFullyGone()
    {
        var a = new ComputerControlActivityService();
        var ind = StartIndicator(a);
        try
        {
            var c = a.AttachClient();
            WaitForStrips(ind, _fx.Inbrisk.Monitors().Count * 4);
            Assert.True(SpinWait.SpinUntil(
                () => Math.Abs(ind.RenderedGlowAlpha - 0.15) < 0.02, 3000),
                "idle glow did not settle");

            c.Dispose();
            // fade-out must decay the rendered alpha monotonically and the
            // strips must be destroyed shortly after — no lingering haze
            var samples = new List<double>();
            for (var i = 0; i < 12 && ind.StripCount > 0; i++)
            {
                samples.Add(ind.RenderedGlowAlpha);
                Thread.Sleep(30);
            }
            Assert.True(SpinWait.SpinUntil(() => ind.StripCount == 0, 1500),
                "strips survived the disconnect fade-out");
            for (var i = 1; i < samples.Count; i++)
                Assert.True(samples[i] <= samples[i - 1] + 0.001,
                    $"fade-out jumped up: {samples[i - 1]:F3} → {samples[i]:F3}");
            Assert.True(ind.RenderedGlowAlpha < 0.02,
                $"glow did not fade to transparent: {ind.RenderedGlowAlpha:F3}");
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    // --------------------------------------------------------------
    // Glow metrics + breathing wave — pure math
    // --------------------------------------------------------------

    [Fact]
    public void Metrics_AdaptToMonitorSizeAndDpi()
    {
        // 1080p → sigma ~4.5px, strip ~14px compact halo; 4K scales up so
        // the physical appearance stays constant; small laptop hits the floor
        var (sig1080, s1080) = GlowMetrics.MetricsFor(1920, 1080);
        Assert.InRange(sig1080, 4.0, 9.0);
        Assert.InRange(s1080, 12, 28);
        var (sig4k, s4k) = GlowMetrics.MetricsFor(3840, 2160);
        Assert.True(sig4k >= sig1080 && s4k >= s1080,
            "4K should get a proportionally wider falloff");
        var (sigSmall, sSmall) = GlowMetrics.MetricsFor(1366, 768);
        Assert.True(sigSmall >= 4.0 && sSmall >= 12);
    }

    [Fact]
    public void Breath_IsSmooth_AndBounded()
    {
        // cosine ease: 0 at 0°, peak at 180°, back to 0 at 360° — no blink
        Assert.Equal(0, GlowMetrics.BreathSample(0), 3);
        Assert.Equal(1, GlowMetrics.BreathSample(180), 3);
        Assert.Equal(0, GlowMetrics.BreathSample(360), 3);
        // strictly continuous — adjacent samples differ by < 1%
        var prev = GlowMetrics.BreathSample(0);
        for (var d = 1; d <= 360; d++)
        {
            var v = GlowMetrics.BreathSample(d);
            Assert.InRange(v, 0, 1);
            Assert.True(Math.Abs(v - prev) < 0.02,
                $"breath jump at {d}°: {prev} → {v}");
            prev = v;
        }
    }

    // --------------------------------------------------------------
    // Live overlay on the real desktop
    // --------------------------------------------------------------

    private ScreenIndicatorService StartIndicator(ComputerControlActivityService a)
    {
        var ind = new ScreenIndicatorService(animFrameMs: 30);
        a.StateChanged += ind.SetState;
        ind.Start();
        Assert.True(ind.WaitForReady());
        return ind;
    }

    private IntPtr WaitForStrips(ScreenIndicatorService ind, int count, int ms = 5000)
    {
        Assert.True(SpinWait.SpinUntil(() => ind.StripCount == count, ms),
            $"expected {count} strips, got {ind.StripCount}");
        return ind.StripHwnds[0];
    }

    [Fact]
    public void Overlay_ConnectedIdle_ShowsClickThroughStripsOnEveryMonitor()
    {
        var a = new ComputerControlActivityService();
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            var monitors = _fx.Inbrisk.Monitors();
            var hwnd = WaitForStrips(ind, monitors.Count * 4);

            foreach (var h in ind.StripHwnds)
            {
                Assert.True(IsWindow(h));
                var ex = (int)(long)GetWindowLongPtr(h, ExStyle);
                Assert.True((ex & WsExTransparent) != 0);   // click-through
                Assert.True((ex & WsExLayered) != 0);
                Assert.True((ex & WsExTopmost) != 0);
                Assert.True((ex & WsExToolwindow) != 0);    // no taskbar/Alt+Tab
                Assert.True((ex & WsExNoactivate) != 0);    // never steals focus
                Assert.True(GetWindowRect(h, out var r));
                var w = r.R - r.L; var hh = r.B - r.T;
                Assert.True(Math.Min(w, hh) <= 40,
                    $"strip too thick: {w}x{hh}"); // core + feathered glow only
                // inside some monitor's bounds
                Assert.Contains(monitors, m =>
                    r.L >= m.Bounds.X - 1 && r.R <= m.Bounds.X + m.Bounds.Width + 1 &&
                    r.T >= m.Bounds.Y - 1 && r.B <= m.Bounds.Y + m.Bounds.Height + 1);
            }
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    [Fact]
    public void Overlay_Active_AdvancesPhase_EmergencyStopsIt()
    {
        var a = new ComputerControlActivityService();
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            WaitForStrips(ind, _fx.Inbrisk.Monitors().Count * 4);

            var lease = a.BeginActivity();
            Assert.True(SpinWait.SpinUntil(() => ind.CurrentPhase > 10, 3000),
                "breathing phase did not advance in Active state");

            a.SetEmergency(true);
            Thread.Sleep(120); // let the stop land
            var p1 = ind.CurrentPhase;
            Thread.Sleep(250);
            Assert.Equal(p1, ind.CurrentPhase); // animation hard-stopped

            // emergency is a red GLOW, not a red line: edge pixel is red-
            // dominant and the profile feathers inward like the lime state
            var em = ind.DebugReadStripPixels(0);
            Assert.NotNull(em);
            Assert.True(em[2] > em[1] + 40 && em[1] >= em[0],
                $"emergency edge not red: R{em[2]} G{em[1]} B{em[0]}");
            Assert.True(em[3] > 60, $"emergency glow too faint: {em[3]}");

            a.SetEmergency(false);
            Assert.True(SpinWait.SpinUntil(() => ind.CurrentPhase > p1, 3000),
                "animation did not resume after emergency cleared");
            lease.Dispose();
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    [Fact]
    public void Overlay_ActiveToIdle_SettlesSmoothly_NoJump()
    {
        var a = new ComputerControlActivityService(idleGraceMs: 150);
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            WaitForStrips(ind, _fx.Inbrisk.Monitors().Count * 4);

            using (var lease = a.BeginActivity())
                Assert.True(SpinWait.SpinUntil(
                    () => ind.RenderedGlowAlpha > 0.25, 3000),
                    "breathing never brightened in Active");
            // lease disposed → grace → ConnectedIdle; the glow must decay
            // continuously toward the idle endpoint, never snap
            Assert.True(SpinWait.SpinUntil(
                () => a.State == IndicatorState.ConnectedIdle, 3000));
            var samples = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                samples.Add(ind.RenderedGlowAlpha);
                Thread.Sleep(40);
            }
            // settle is a descent toward ~0.15 — no sample may jump UP
            // by more than a tick's worth of breathing would explain
            for (var i = 1; i < samples.Count; i++)
                Assert.True(samples[i] <= samples[i - 1] + 0.02,
                    $"glow jumped during settle: {samples[i - 1]:F3} → {samples[i]:F3}");
            Assert.True(SpinWait.SpinUntil(
                () => Math.Abs(ind.RenderedGlowAlpha - 0.15) < 0.02, 3000),
                $"settle did not converge to idle glow (at {ind.RenderedGlowAlpha:F3})");
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    [Fact]
    public void Overlay_IdlePixels_AreLimeGlow_NoLine()
    {
        var a = new ComputerControlActivityService();
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            WaitForStrips(ind, _fx.Inbrisk.Monitors().Count * 4);
            Thread.Sleep(300); // first render + settle

            // strip 0 is the Top strip of the first monitor: row 0 is the
            // screen edge (bright core), rows deeper feather to transparent
            var size = ind.DebugStripSize(0);
            Assert.NotNull(size);
            var px = ind.DebugReadStripPixels(0);
            Assert.NotNull(px);
            var (w, h) = size.Value;
            byte A(int row) => px[row * w * 4 + 3];
            byte B(int row) => px[row * w * 4 + 0];
            byte G(int row) => px[row * w * 4 + 1];
            byte R(int row) => px[row * w * 4 + 2];

            // the gaussian peaks at the edge — translucent smoke, NOT a
            // solid line: idle edge intensity ~0.15 and alpha must already
            // be meaningfully decaying within the first few rows
            Assert.True(A(0) > 25, $"edge alpha too faint: {A(0)}");
            Assert.True(A(0) < 60, $"edge alpha too harsh: {A(0)}");
            Assert.True(G(0) > R(0) && R(0) > B(0),
                $"not lime at edge: R{R(0)} G{G(0)} B{B(0)}");
            Assert.True(A(4) <= A(0) * 0.80,
                $"flat top — a visible line: A(0)={A(0)} A(4)={A(4)}");

            // inward feather: alpha must decay smoothly toward zero, never
            // jump — that's the soft glow, not a hard border
            var prev = A(0);
            for (var row = 1; row < h; row++)
            {
                Assert.True(A(row) <= prev + 4,
                    $"alpha jumped at row {row}: {prev} → {A(row)}");
                prev = A(row);
            }
            Assert.True(A(h - 1) < 10,
                $"glow never faded: innermost alpha {A(h - 1)}");
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    [Fact]
    public void Overlay_EmergencyToIdle_RepaintsPixels_NoStaleRed()
    {
        var a = new ComputerControlActivityService(idleGraceMs: 150);
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            WaitForStrips(ind, _fx.Inbrisk.Monitors().Count * 4);
            Assert.True(SpinWait.SpinUntil(
                () => Math.Abs(ind.RenderedGlowAlpha - 0.15) < 0.02, 3000));

            a.SetEmergency(true);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                var em = ind.DebugReadStripPixels(0);
                return em != null && em[2] > em[1] + 40; // red-dominant
            }, 3000), "emergency pixels never went red");

            // resume → ConnectedIdle: _intA is already at the idle target,
            // so Chase converges instantly — the bug was the tick parking
            // WITHOUT repainting, leaving the red framebuffer on screen
            a.SetEmergency(false);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                var px = ind.DebugReadStripPixels(0);
                return px != null && px[1] > px[2] && px[2] > px[0]; // lime G>R>B
            }, 3000), "stale red framebuffer survived resume to ConnectedIdle");
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    [Fact]
    public void Overlay_EmergencyToActive_RepaintsPixels_AndBreathes()
    {
        var a = new ComputerControlActivityService();
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            WaitForStrips(ind, _fx.Inbrisk.Monitors().Count * 4);

            var lease = a.BeginActivity(); // real activity held through the cycle
            a.SetEmergency(true);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                var em = ind.DebugReadStripPixels(0);
                return em != null && em[2] > em[1] + 40;
            }, 3000), "emergency pixels never went red");

            // resume while the lease is still held → must land in Active
            // (lime breathing), never stick on a stale red or idle frame
            a.SetEmergency(false);
            Assert.Equal(IndicatorState.Active, a.State);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                var px = ind.DebugReadStripPixels(0);
                return px != null && px[1] > px[2] && px[2] > px[0];
            }, 3000), "stale red framebuffer survived resume to Active");
            var p1 = ind.CurrentPhase;
            Assert.True(SpinWait.SpinUntil(() => ind.CurrentPhase > p1, 2000),
                "breathing did not resume after EmergencyStopped → Active");
            lease.Dispose();
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    [Fact]
    public void Overlay_Disconnected_RemovesAllStrips()
    {
        var a = new ComputerControlActivityService();
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            var monitors = _fx.Inbrisk.Monitors();
            WaitForStrips(ind, monitors.Count * 4);
            var hwnds = ind.StripHwnds;

            a.SetConnected(false);
            Assert.True(SpinWait.SpinUntil(() => ind.StripCount == 0, 3000));
            Thread.Sleep(100);
            foreach (var h in hwnds)
                Assert.False(IsWindow(h), "strip window survived disconnect");
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    [Fact]
    public void Overlay_ExcludedFromCapture_BorderNotInScreenshot()
    {
        var a = new ComputerControlActivityService();
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            var monitors = _fx.Inbrisk.Monitors();
            WaitForStrips(ind, monitors.Count * 4);
            Thread.Sleep(200); // let DWM settle the affinity

            Assert.True(ind.CaptureExclusionSupported,
                "WDA_EXCLUDEFROMCAPTURE rejected — report as platform limitation");
            foreach (var h in ind.StripHwnds)
            {
                Assert.True(GetWindowDisplayAffinity(h, out var aff));
                Assert.Equal(0x11u, aff); // WDA_EXCLUDEFROMCAPTURE
            }

            // capture the top strip region of the primary monitor — the lime
            // glow must NOT appear in Inbrisk's own capture
            var mon = monitors.First(m => m.IsPrimary).Bounds;
            var region = new RectPx(mon.X, mon.Y, mon.Width, 30);
            var raw = _fx.Inbrisk.CaptureRaw(new CaptureTarget.Region(region));
            var limePixels = 0;
            for (var i = 0; i + 3 < raw.Bgra.Length; i += 4)
                if (raw.Bgra[i] is < 120 && raw.Bgra[i + 1] > 220 &&
                    raw.Bgra[i + 2] > 150) // B7FF3C..D7FF78 within tolerance
                    limePixels++;
            var total = raw.Width * raw.Height;
            Assert.True(limePixels < total / 100,
                $"indicator lime leaked into capture: {limePixels}/{total} px");
        }
        finally { ind.Dispose(); a.Dispose(); }
    }

    [Fact]
    public void Overlay_TopologyRebuild_KeepsStripCountConsistent()
    {
        var a = new ComputerControlActivityService();
        var ind = StartIndicator(a);
        try
        {
            a.SetConnected(true);
            var monitors = _fx.Inbrisk.Monitors();
            WaitForStrips(ind, monitors.Count * 4);
            ind.RefreshTopology();
            Thread.Sleep(400);
            Assert.Equal(monitors.Count * 4, ind.StripCount);
        }
        finally { ind.Dispose(); a.Dispose(); }
    }
}
