using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Pluggable post-action verification. Honest: returns Unverified when no
/// observable evidence exists rather than claiming success.
/// </summary>
public sealed class Verifier
{
    private readonly IWindowService _windows;
    private readonly ICaptureService _capture;

    public Verifier(IWindowService windows, ICaptureService capture)
    {
        _windows = windows;
        _capture = capture;
    }

    public VerifyResult Verify(VerifySpec spec, UiElement? element, WindowInfo? window)
    {
        try
        {
            return spec.Kind switch
            {
                VerifyKind.ForegroundIs => VerifyForeground(spec),
                VerifyKind.RegionChanged => VerifyRegionChanged(spec),
                VerifyKind.RegionStable => VerifyRegionStable(spec),
                VerifyKind.ElementGone or VerifyKind.ElementExists
                    => VerifyResult.Unverified, // needs backend re-query — wired via Executor callers
                VerifyKind.PropertyEquals => VerifyResult.Unverified, // re-read happens in Sdk layer
                _ => VerifyResult.NotRequested,
            };
        }
        catch { return VerifyResult.Unverified; }
    }

    private VerifyResult VerifyForeground(VerifySpec spec)
    {
        var deadline = DateTime.Now.AddMilliseconds(spec.TimeoutMs);
        while (DateTime.Now < deadline)
        {
            var fg = _windows.GetForegroundWindow();
            if (fg != null && spec.Hwnd is { } h && fg.Hwnd == h) return VerifyResult.Verified;
            Thread.Sleep(100);
        }
        return VerifyResult.Failed;
    }

    private VerifyResult VerifyRegionChanged(VerifySpec spec)
    {
        if (spec.Region is not { } region) return VerifyResult.Unverified;
        var deadline = DateTime.Now.AddMilliseconds(spec.TimeoutMs);
        while (DateTime.Now < deadline)
        {
            if (_capture.DiffFraction(region) > 0.005) return VerifyResult.Verified;
            Thread.Sleep(80);
        }
        return VerifyResult.Failed;
    }

    private VerifyResult VerifyRegionStable(VerifySpec spec)
    {
        if (spec.Region is not { } region) return VerifyResult.Unverified;
        var quietUntil = DateTime.Now.AddMilliseconds(Math.Min(1200, spec.TimeoutMs));
        var deadline = DateTime.Now.AddMilliseconds(spec.TimeoutMs);
        while (DateTime.Now < deadline)
        {
            if (_capture.DiffFraction(region) > 0.005) quietUntil = DateTime.Now.AddMilliseconds(1200);
            else if (DateTime.Now > quietUntil) return VerifyResult.Verified;
            Thread.Sleep(80);
        }
        return VerifyResult.Failed;
    }
}
