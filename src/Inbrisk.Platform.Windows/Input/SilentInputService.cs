using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Input;

/// <summary>
/// <see cref="ISilentInputService"/> over the static <see cref="SilentInput"/>
/// engine. The Runtime assembly cannot reference this one, so the executor's
/// silent dispatch talks to the Core contract and this adapter maps the
/// types across the boundary.
/// </summary>
public sealed class SilentInputService : ISilentInputService
{
    public bool IsSupported(ActionKind kind, out string detail)
        => SilentInput.IsSupported(kind, out detail);

    public SilentInputResult TryClick(SilentTargetRef target)
        => Map(SilentInput.TryClick(Map(target)));

    public SilentInputResult TrySetText(SilentTargetRef target, string text)
        => Map(SilentInput.TrySetText(Map(target), text));

    public SilentInputResult TrySetCheck(SilentTargetRef target, bool check)
        => Map(SilentInput.TrySetCheck(Map(target), check));

    public SilentInputResult TryClose(long hwnd)
        => Map(SilentInput.TryClose(new IntPtr(hwnd)));

    private static SilentInput.SilentTarget Map(SilentTargetRef t) => new(
        ElementHwnd: t.ElementHwnd is { } e ? new IntPtr(e) : null,
        WindowHwnd: t.WindowHwnd is { } w ? new IntPtr(w) : null,
        AutomationId: t.AutomationId,
        Name: t.Name,
        Bounds: t.Bounds,
        CommandId: t.CommandId);

    private static SilentInputResult Map(SilentInput.SilentResult r) => new(
        r.Ok,
        r.Status switch
        {
            SilentInput.SilentStatus.Ok => SilentInputStatus.Ok,
            SilentInput.SilentStatus.Timeout => SilentInputStatus.Timeout,
            SilentInput.SilentStatus.Unsupported => SilentInputStatus.Unsupported,
            _ => SilentInputStatus.Failed,
        },
        r.Method, r.Verified, r.Detail);
}
