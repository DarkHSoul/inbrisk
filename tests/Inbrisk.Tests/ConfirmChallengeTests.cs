using Inbrisk.Core;
using Inbrisk.Runtime;

namespace Inbrisk.Tests;

[Trait("Category", "Unit")]
public sealed class ConfirmChallengeTests
{
    private static WindowInfo MakeWindow(long hwnd, string proc, string title = "Terminal") =>
        new(hwnd, 1234, title, proc, new RectPx(0, 0, 800, 600),
            WindowState.Normal, true, false, true, 0);

    private static ActionIntent TypeIntent(long hwnd, string text) =>
        new(ActionKind.TypeText, TargetRef.Window(hwnd),
            new Dictionary<string, object?> { ["text"] = text });

    [Fact]
    public void IssueConfirmation_CreatesPendingChallengeAndDeduplicatesWithinTtl()
    {
        var policy = new SafetyPolicy();
        var win = MakeWindow(100, "powershell");
        var intent = TypeIntent(100, "echo hello");

        var ch1 = policy.IssueConfirmation(intent, null, win);
        Assert.StartsWith("cnf-", ch1.ApprovalId);
        Assert.Equal(ConfirmChallengeState.Pending, ch1.State);
        Assert.False(ch1.IsExpired);
        Assert.NotEmpty(ch1.ActionHash);
        Assert.Contains("TypeText", ch1.Description);
        Assert.Contains(ch1, policy.PendingConfirmations);

        // Same action+target inside TTL must reuse the same pending challenge
        var ch2 = policy.IssueConfirmation(intent, null, win);
        Assert.Same(ch1, ch2);
        Assert.Single(policy.PendingConfirmations);
    }

    [Fact]
    public void TryConsumeConfirmation_RequiresApproval_AndIsSingleUse()
    {
        var policy = new SafetyPolicy();
        var win = MakeWindow(100, "powershell");
        var intent = TypeIntent(100, "dir");

        var ch = policy.IssueConfirmation(intent, null, win);

        // Pending cannot be consumed
        Assert.False(policy.TryConsumeConfirmation(intent, null, win, ch.ApprovalId));
        Assert.Equal(ConfirmChallengeState.Pending, ch.State);

        // Local approval transitions to Approved
        Assert.True(policy.ApproveConfirmation(ch.ApprovalId));
        Assert.Equal(ConfirmChallengeState.Approved, ch.State);

        // First consumption succeeds and marks Consumed
        Assert.True(policy.TryConsumeConfirmation(intent, null, win, ch.ApprovalId));
        Assert.Equal(ConfirmChallengeState.Consumed, ch.State);

        // Replay MUST fail
        Assert.False(policy.TryConsumeConfirmation(intent, null, win, ch.ApprovalId));
        // Re-approving a consumed grant MUST fail
        Assert.False(policy.ApproveConfirmation(ch.ApprovalId));
    }

    [Fact]
    public void TryConsumeConfirmation_BoundToExactActionAndTargetFingerprint()
    {
        var policy = new SafetyPolicy();
        var win = MakeWindow(100, "powershell");
        var safeIntent = TypeIntent(100, "git status");
        var evilIntent = TypeIntent(100, "rm -rf /");
        var otherWindow = MakeWindow(200, "powershell");

        var ch = policy.IssueConfirmation(safeIntent, null, win);
        Assert.True(policy.ApproveConfirmation(ch.ApprovalId));

        // Different text argument -> rejected
        Assert.False(policy.TryConsumeConfirmation(evilIntent, null, win, ch.ApprovalId));
        // Different window -> rejected
        Assert.False(policy.TryConsumeConfirmation(safeIntent, null, otherWindow, ch.ApprovalId));
        // Original grant still unconsumed and valid for original action
        Assert.Equal(ConfirmChallengeState.Approved, ch.State);
        Assert.True(policy.TryConsumeConfirmation(safeIntent, null, win, ch.ApprovalId));
    }

    [Fact]
    public void DenyConfirmation_PreventsConsumptionAndRemovesFromPending()
    {
        var policy = new SafetyPolicy();
        var win = MakeWindow(100, "cmd");
        var intent = TypeIntent(100, "del *.*");

        var ch = policy.IssueConfirmation(intent, null, win);
        Assert.True(policy.DenyConfirmation(ch.ApprovalId));
        Assert.Equal(ConfirmChallengeState.Denied, ch.State);
        Assert.Empty(policy.PendingConfirmations);
        Assert.False(policy.TryConsumeConfirmation(intent, null, win, ch.ApprovalId));
    }

    [Fact]
    public void ExpiredChallenge_CannotBeApprovedOrConsumed()
    {
        var policy = new SafetyPolicy { ConfirmChallengeTtl = TimeSpan.FromMilliseconds(15) };
        var win = MakeWindow(100, "pwsh");
        var intent = TypeIntent(100, "whoami");

        var ch = policy.IssueConfirmation(intent, null, win);
        Thread.Sleep(40);

        Assert.True(ch.IsExpired);
        Assert.Empty(policy.PendingConfirmations);
        Assert.False(policy.ApproveConfirmation(ch.ApprovalId));
        Assert.False(policy.TryConsumeConfirmation(intent, null, win, ch.ApprovalId));
    }

    [Theory]
    [InlineData("powershell", ActionKind.TypeText, true)]
    [InlineData("cmd", ActionKind.KeyPress, true)]
    [InlineData("pwsh", ActionKind.SetValue, true)]
    [InlineData("windowsterminal", ActionKind.Hotkey, true)]
    [InlineData("notepad", ActionKind.TypeText, false)]
    public void IsDangerous_FlagsShellInputAndIgnoresBenignApps(
        string processName, ActionKind kind, bool expectedDangerous)
    {
        var policy = new SafetyPolicy { AutoConfirm = true };
        var win = MakeWindow(100, processName);
        var intent = new ActionIntent(kind, TargetRef.Window(100),
            new Dictionary<string, object?> { ["text"] = "test", ["key"] = "A" });

        Assert.Equal(expectedDangerous, policy.IsDangerous(intent, null, win));
    }

    [Theory]
    [InlineData("F4", new[] { "Alt" }, true)]
    [InlineData("W", new[] { "Ctrl" }, true)]
    [InlineData("F4", new[] { "Ctrl" }, true)]
    [InlineData("Q", new[] { "Ctrl" }, true)]
    [InlineData("Alt+F4", null, true)]
    [InlineData("S", new[] { "Ctrl" }, false)]
    public void IsDangerous_FlagsClosingChordsRegardlessOfAutoConfirm(
        string key, string[]? modifiers, bool expectedDangerous)
    {
        var policy = new SafetyPolicy { AutoConfirm = true };
        var win = MakeWindow(100, "notepad");
        var args = new Dictionary<string, object?> { ["key"] = key };
        if (modifiers != null) args["modifiers"] = modifiers;
        var intent = new ActionIntent(ActionKind.Hotkey, TargetRef.Window(100), args);

        Assert.Equal(expectedDangerous, policy.IsDangerous(intent, null, win));
    }
}
