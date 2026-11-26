using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Per-window keyboard grant: the user's answer to an elicitation prompt
/// lets keyboard-class input into ONE window through the CONFIRM gate until
/// that window is gone. Pins what it allows and, more importantly, what it
/// must not: other windows, dangerous classes, declined/unavailable prompts.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WindowInputGrantTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();

    // Real, live window handles so the IsWindow liveness check holds.
    private static readonly long HwndA = GetDesktopWindow().ToInt64();
    private static readonly long HwndB = GetShellWindow().ToInt64();
    private static readonly int Pid = Process.GetCurrentProcess().Id;

    private static WindowInfo Win(long hwnd, int? pid = null) => new(hwnd, pid ?? Pid,
        "Minecraft* 26.2", "javaw", new RectPx(0, 0, 800, 600),
        WindowState.Normal, true, false, true, 0);

    private static ActionIntent Key() => new(ActionKind.KeyPress, TargetRef.At(0, 0));

    private static ConfirmChannel Channel(Func<bool?> answer, out int[] prompts,
        int delayMs = 0)
    {
        var count = new int[1];
        prompts = count;
        var ch = new ConfirmChannel("test");
        ch.WindowConsentPrompt = async (_, _) =>
        {
            Interlocked.Increment(ref count[0]);
            if (delayMs > 0) await Task.Delay(delayMs);
            return answer();
        };
        return ch;
    }

    [Fact]
    public void Accept_grants_the_window_and_later_keys_do_not_prompt()
    {
        var ch = Channel(() => true, out var prompts);
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.True(ch.Confirmer(new ActionIntent(ActionKind.Hotkey, TargetRef.At(0, 0)),
            null, Win(HwndA)));
        Assert.Equal(1, prompts[0]);
        Assert.Single(ch.WindowGrants);
    }

    [Fact]
    public void Grant_is_scoped_to_one_window()
    {
        var ch = Channel(() => true, out var prompts);
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.True(ch.Confirmer(Key(), null, Win(HwndB)));
        Assert.Equal(2, prompts[0]); // the second window asked for itself
    }

    [Fact]
    public void Decline_denies_and_does_not_reprompt_during_cooldown()
    {
        var ch = Channel(() => false, out var prompts, delayMs: 400); // human-speed "no"
        Assert.False(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.False(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.Equal(1, prompts[0]);
        Assert.Empty(ch.WindowGrants);
    }

    [Fact]
    public void Instant_client_decline_is_not_a_user_choice_and_pauses_prompting()
    {
        var ch = Channel(() => false, out var prompts); // answers in ~0 ms
        Assert.False(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.False(ch.Confirmer(Key(), null, Win(HwndB)));
        Assert.Equal(1, prompts[0]); // prompting paused after the first instant answer
    }

    [Fact]
    public void Tray_can_allow_a_pending_window_until_it_closes()
    {
        var ch = new ConfirmChannel("test"); // no elicitation channel at all
        Assert.False(ch.Confirmer(Key(), null, Win(HwndA)));
        var pending = Assert.Single(ch.Pending);
        Assert.True(pending.CanAllowWindow);

        Assert.True(ch.ApproveWindowLocal(pending.Id));
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.True(ch.Confirmer(
            new ActionIntent(ActionKind.Hotkey, TargetRef.At(0, 0)), null, Win(HwndA)));
        Assert.Empty(ch.Pending);

        Assert.False(ch.Confirmer(Key(), null, Win(HwndB))); // other window still gated
        Assert.True(ch.RevokeWindow(HwndA, "test"));
        Assert.False(ch.Confirmer(Key(), null, Win(HwndA)));
    }

    [Fact]
    public void Tray_window_approval_refuses_dangerous_and_unknown_ids()
    {
        var ch = new ConfirmChannel("test");
        Assert.False(ch.LocalConsent(Key(), null, Win(HwndA)));
        var dangerous = Assert.Single(ch.Pending);
        Assert.False(dangerous.CanAllowWindow);
        Assert.False(ch.ApproveWindowLocal(dangerous.Id));
        Assert.False(ch.ApproveWindowLocal("appr_doesnotexist"));
        Assert.Empty(ch.WindowGrants);
    }

    [Fact]
    public void Unavailable_prompt_fails_closed()
    {
        var ch = Channel(() => null, out _);
        Assert.False(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.Empty(ch.WindowGrants);

        var noPrompt = new ConfirmChannel("test");
        Assert.False(noPrompt.Confirmer(Key(), null, Win(HwndA)));
    }

    [Fact]
    public void Dangerous_class_never_uses_or_creates_a_grant()
    {
        var ch = Channel(() => true, out var prompts);
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));       // granted
        Assert.False(ch.LocalConsent(Key(), null, Win(HwndA)));   // still one-shot only
        Assert.Equal(1, prompts[0]);
    }

    [Fact]
    public void Non_keyboard_actions_do_not_ride_the_grant()
    {
        var ch = Channel(() => true, out var prompts);
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.False(ch.Confirmer(
            new ActionIntent(ActionKind.ClipboardRead, TargetRef.At(0, 0)),
            null, Win(HwndA)));
        Assert.Equal(1, prompts[0]);
    }

    [Fact]
    public void Revoke_on_window_close_requires_a_new_answer()
    {
        var ch = Channel(() => true, out var prompts);
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.True(ch.RevokeWindow(HwndA));
        Assert.Empty(ch.WindowGrants);
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));
        Assert.Equal(2, prompts[0]);
    }

    [Fact]
    public void Grant_does_not_survive_a_different_process_owning_the_handle()
    {
        var ch = Channel(() => true, out var prompts);
        Assert.True(ch.Confirmer(Key(), null, Win(HwndA)));
        // same hwnd now reported under another pid â†’ grant must not apply
        Assert.False(ch.Confirmer(Key(), null, Win(HwndA, pid: Pid + 100000)));
        Assert.Equal(1, prompts[0]); // no start time for a bogus pid â†’ no prompt
    }
}
