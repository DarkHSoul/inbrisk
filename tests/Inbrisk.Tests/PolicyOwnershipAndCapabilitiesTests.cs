using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Window-ownership-aware safety classification + the capability contract
/// that tells a client the rules exist.
///
/// Context: every TypeText/SetValue was CONFIRM-class unconditionally.
/// With AutoConfirm off and no local consent surface wired, that meant an
/// agent that launched Notepad itself could not type into the window it
/// had just opened — the action denied with an approvalId no MCP tool can
/// approve. These tests pin the narrowed exemption AND, just as
/// importantly, pin everything it must NOT let through.
/// </summary>
public sealed class PolicyOwnershipAndCapabilitiesTests
{
    private const long AgentHwnd = 0x2D0772;
    private const long UserHwnd = 0x140734;
    private const int AgentPid = 18764;
    private const int UserPid = 9999;

    private static WindowInfo Win(long hwnd, int pid, string title, string proc,
        bool elevated = false) => new(hwnd, pid, title, proc,
        new RectPx(0, 0, 800, 600), WindowState.Normal, true, elevated, true, 0);

    private static ActionIntent TypeText() =>
        new(ActionKind.TypeText, TargetRef.At(0, 0),
            new Dictionary<string, object?> { ["text"] = "Merhaba" });

    private static ActionIntent Hotkey(string key = "ctrl+s") =>
        new(ActionKind.Hotkey, TargetRef.At(0, 0),
            new Dictionary<string, object?> { ["key"] = key });

    private static (SafetyPolicy Policy, ProcessProvenanceService Prov) Policy()
    {
        var prov = new ProcessProvenanceService();
        return (new SafetyPolicy { Provenance = prov }, prov);
    }

    // ---------- the fix: text entry into a window THIS agent opened ----------

    [Fact]
    public void TypeText_IntoAgentOwnedWindow_IsSafe_AndSaysWhy()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");

        var d = policy.Evaluate(TypeText(), null, Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad"));

        Assert.Equal(SafetyClass.Safe, d.Class);
        Assert.Equal(PolicyReason.AgentOwnedWindowTextEntry, d.Reason);
        Assert.Equal("AgentOwnedWindowTextEntry", d.Code);
    }

    [Fact]
    public void SetValue_IntoAgentOwnedWindow_IsSafe()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");

        var intent = new ActionIntent(ActionKind.SetValue, TargetRef.At(0, 0),
            new Dictionary<string, object?> { ["text"] = "Merhaba" });
        var d = policy.Evaluate(intent, null, Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad"));

        Assert.Equal(SafetyClass.Safe, d.Class);
        Assert.Equal(PolicyReason.AgentOwnedWindowTextEntry, d.Reason);
    }

    // ---------- what must NOT be relaxed ----------

    [Fact]
    public void TypeText_IntoAgentLaunchedShell_StaysDangerousConfirm()
    {
        // The agent launched pwsh itself. Ownership says nothing about
        // what typing into it MEANS — it is still command execution, and
        // it must still reach the executor's LocalConsent gate.
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(4242, "pwsh", 0x5555, "pwsh");

        var d = policy.Evaluate(TypeText(), null, Win(0x5555, 4242, "PowerShell", "pwsh"));

        Assert.Equal(SafetyClass.Confirm, d.Class);
        Assert.Equal(PolicyReason.DangerousShellInput, d.Reason);
        Assert.True(policy.IsDangerous(TypeText(), null, Win(0x5555, 4242, "PowerShell", "pwsh")));
    }

    [Fact]
    public void Hotkey_IntoAgentOwnedWindow_StaysConfirm()
    {
        // Only TEXT entry was narrowed. A hotkey is a command, not a value.
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");

        var d = policy.Evaluate(Hotkey(), null, Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad"));

        Assert.Equal(SafetyClass.Confirm, d.Class);
        Assert.Equal(PolicyReason.KeystrokeIntoWindow, d.Reason);
    }

    [Fact]
    public void ClosingChord_IntoAgentOwnedWindow_StaysDangerous()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        var w = Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad");

        var d = policy.Evaluate(Hotkey("alt+F4"), null, w);

        Assert.Equal(SafetyClass.Confirm, d.Class);
        Assert.True(policy.IsDangerous(Hotkey("alt+F4"), null, w));
    }

    [Fact]
    public void ClipboardTransfer_StaysConfirm_InAgentOwnedWindowToo()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        var w = Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad");

        foreach (var kind in new[] { ActionKind.ClipboardRead, ActionKind.ClipboardWrite })
        {
            var d = policy.Evaluate(new ActionIntent(kind, TargetRef.At(0, 0)), null, w);
            Assert.Equal(SafetyClass.Confirm, d.Class);
            Assert.Equal(PolicyReason.ClipboardTransfer, d.Reason);
        }
    }

    [Fact]
    public void KillSwitch_Denies_Everything_IncludingAgentOwnedWindow()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        policy.KillSwitch = true;
        var w = Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad", elevated: true);

        var d = policy.Evaluate(TypeText(), null, w);

        Assert.Equal(SafetyClass.Deny, d.Class);
        Assert.Equal(PolicyReason.KillSwitch, d.Reason);
    }

    [Fact]
    public void ElevatedTarget_IsArbitratedByExecutorIntegrityGuard_NotPolicy()
    {
        // A blanket "target is elevated → deny" deliberately no longer lives
        // in the policy: the executor's integrity guard (CheckTarget) is the
        // arbiter of the UIPI relation — whether the TARGET sits above OUR
        // integrity level. A same-integrity elevated target — an agent under
        // an elevated shell typing into an app it launched itself — stays
        // ordinary agent work; a genuinely higher-integrity target is
        // refused by the executor before policy evaluation even runs.
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        var w = Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad", elevated: true);

        var d = policy.Evaluate(TypeText(), null, w);

        Assert.Equal(SafetyClass.Safe, d.Class);
        Assert.Equal(PolicyReason.AgentOwnedWindowTextEntry, d.Reason);
    }

    [Fact]
    public void DenyListedProcess_Denied_EvenInAgentOwnedWindow()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(77, "calc.exe", 0x8888, "calc");
        policy.DenyProcessNames.Add("calc.exe");

        var d = policy.Evaluate(TypeText(), null, Win(0x8888, 77, "Calculator", "calc.exe"));

        Assert.Equal(SafetyClass.Deny, d.Class);
        Assert.Equal(PolicyReason.DenyListedProcess, d.Reason);
    }

    [Fact]
    public void PasswordField_Denied_ForTextEntry_InAgentOwnedWindowToo()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        var el = new UiElement("uia_1", BackendId.Uia, Role.Edit, "Password",
            new RectPx(0, 0, 100, 20), Array.Empty<string>(),
            new Dictionary<string, object?> { ["isPassword"] = true },
            Handle: null!, null!, AgentPid, AgentHwnd);

        var d = policy.Evaluate(TypeText(), el, Win(AgentHwnd, AgentPid, "x", "Notepad"));

        Assert.Equal(SafetyClass.Deny, d.Class);
        Assert.Equal(PolicyReason.PasswordField, d.Reason);
    }

    // ---------- the reused-user-window case (the one that reported PolicyDenied) ----------

    [Fact]
    public void TypeText_IntoUserOwnedWindow_StaysConfirm_WithOwnershipReason()
    {
        var (policy, prov) = Policy();
        prov.RegisterInitial(UserPid, "notepad.exe", UserHwnd);

        var d = policy.Evaluate(TypeText(), null,
            Win(UserHwnd, UserPid, "Modelfile, Not Defteri", "notepad.exe"));

        Assert.Equal(SafetyClass.Confirm, d.Class);
        Assert.Equal(PolicyReason.UserOwnedWindowTextEntry, d.Reason);
        Assert.Contains("ownership: user", d.Detail);
    }

    [Fact]
    public void TypeText_IntoWindowWithNoProvenance_StaysConfirm_AsUnknown()
    {
        var (policy, _) = Policy();

        var d = policy.Evaluate(TypeText(), null,
            Win(UserHwnd, UserPid, "Modelfile, Not Defteri", "notepad.exe"));

        Assert.Equal(SafetyClass.Confirm, d.Class);
        Assert.Equal(PolicyReason.UnknownWindowTextEntry, d.Reason);
    }

    [Fact]
    public void TypeText_WithNoResolvedWindow_StaysConfirm()
    {
        var (policy, _) = Policy();

        var d = policy.Evaluate(TypeText(), null, null);

        Assert.Equal(SafetyClass.Confirm, d.Class);
        Assert.Equal(PolicyReason.UnknownWindowTextEntry, d.Reason);
    }

    // ---------- the security test: ownership must not be adopted by name ----------

    [Fact]
    public void StrictOwnership_DoesNotAdoptAnotherWindowOfTheSameProcessName()
    {
        // Regression guard for the obvious wrong implementation: reusing
        // GetProvenanceForHwnd (which adopts windows by process name) would
        // answer "agent" for EVERY Notepad window once the agent launched
        // one — including a document the user opened afterwards.
        var prov = new ProcessProvenanceService();
        prov.RegisterAgentLaunch(AgentPid, "notepad", AgentHwnd, "notepad");

        Assert.Equal(StrictWindowOwnership.Agent, prov.GetStrictWindowOwnership(AgentHwnd));

        const long otherUserWindow = 0x5555;
        Assert.Equal(StrictWindowOwnership.Unknown,
            prov.GetStrictWindowOwnership(otherUserWindow));
        Assert.False(prov.CanAgentClose(otherUserWindow, out _));
    }

    [Fact]
    public void StrictOwnership_ReportsUserForRegisteredInitial()
    {
        var prov = new ProcessProvenanceService();
        prov.RegisterInitial(UserPid, "notepad.exe", UserHwnd);

        Assert.Equal(StrictWindowOwnership.User, prov.GetStrictWindowOwnership(UserHwnd));
    }

    // ---------- backwards compatibility ----------

    [Fact]
    public void UnwiredProvenance_KeepsLegacyConfirmOnType()
    {
        // A policy with no provenance source must behave exactly like the
        // pre-ownership one: the exemption can never fire by accident.
        var policy = new SafetyPolicy();

        var d = policy.Evaluate(TypeText(), null,
            Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad"));

        Assert.Equal(SafetyClass.Confirm, d.Class);
    }

    [Fact]
    public void ExemptionCanBeTurnedOff()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        policy.AllowTextEntryIntoAgentOwnedWindow = false;

        var d = policy.Evaluate(TypeText(), null, Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad"));

        Assert.Equal(SafetyClass.Confirm, d.Class);
        Assert.Equal(PolicyReason.AgentOwnedWindowTextEntry, d.Reason);
    }

    [Fact]
    public void ClassifyShim_AgreesWithEvaluate()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        var w = Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad");

        Assert.Equal(policy.Evaluate(TypeText(), null, w).Class,
            policy.Classify(TypeText(), null, w));
    }

    [Fact]
    public void NonGatedKinds_StaySafe()
    {
        var (policy, _) = Policy();

        var d = policy.Evaluate(new ActionIntent(ActionKind.Click, TargetRef.At(0, 0)), null, null);

        Assert.Equal(SafetyClass.Safe, d.Class);
        Assert.Equal(PolicyReason.NotGated, d.Reason);
    }

    // ---------- executor level: does the text actually land? ----------

    private sealed class StubWindows : IWindowService
    {
        private readonly WindowInfo _win;
        public StubWindows(WindowInfo win) => _win = win;
        public IReadOnlyList<WindowInfo> ListWindows() => new[] { _win };
        public WindowInfo? GetWindow(long hwnd) => hwnd == _win.Hwnd ? _win : null;
        public WindowInfo? GetForegroundWindow() => _win;
        public WindowInfo? GetTopLevelWindow(long hwnd) => GetWindow(hwnd);
        public IReadOnlyList<MonitorInfo> GetMonitors() => Array.Empty<MonitorInfo>();
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => null;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => Array.Empty<WindowInfo>();
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private sealed class StubIntegrity : IIntegrityService
    {
        public bool IsSecureDesktopActive() => false;
        public bool CanControlWindow(long hwnd) => true;
        public IntegrityRelation CheckTarget(long hwnd) => IntegrityRelation.Reachable;
    }

    private sealed class StubCapture : ICaptureService
    {
        public Frame Capture(RectPx region, int maxImageWidth = 1600) => throw new NotImplementedException();
        public Frame CaptureWindow(long hwnd, int maxImageWidth = 1600) => throw new NotImplementedException();
        public RawFrame CaptureRaw(RectPx region) => throw new NotImplementedException();
        public double DiffFraction(RectPx region, int sampleScale = 8) => 0;
        public byte[] Sample(RectPx region, int scale = 8) => Array.Empty<byte>();
        public ICaptureSession CreateSession(CaptureTarget target) => throw new NotImplementedException();
    }

    private sealed class RecordingInput : IInputService
    {
        public List<string> Typed { get; } = new();
        public void MoveMouse(int x, int y) { }
        public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1) { }
        public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300, CancellationToken ct = default) { }
        public void Scroll(int x, int y, int wheelDelta) { }
        public void KeyPress(KeyCode key) { }
        public void KeyDown(KeyCode key) { }
        public void KeyUp(KeyCode key) { }
        public void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key) { }
        public void TypeText(string text) => Typed.Add(text);
        public (int X, int Y) CursorPosition() => (0, 0);
        public void ReleaseAll() { }
    }

    private sealed class NullBackend : IElementBackend
    {
        public BackendId Id => BackendId.Uia;
        public IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions options, CancellationToken ct = default) => Array.Empty<UiElement>();
        public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default) => Array.Empty<UiElement>();
        public ActionResult? PerformNative(UiElement element, ActionIntent intent, CancellationToken ct = default) => null;
        public UiElement? ReResolve(ElementHandle handle, CancellationToken ct = default) => null;
        public bool IsAlive(UiElement element) => true;
    }

    private static (Executor Executor, RecordingInput Input) BuildExecutor(
        SafetyPolicy policy, WindowInfo win)
    {
        var input = new RecordingInput();
        var backend = new NullBackend();
        var reg = new ElementRegistry(new[] { backend });
        var exec = new Executor(new StubWindows(win), new StubIntegrity(), input,
            new StubCapture(), new[] { backend }, reg, policy);
        return (exec, input);
    }

    [Fact]
    public void Executor_TypeText_IntoAgentOwnedWindow_ReachesTheKeyboard()
    {
        // The end-to-end proof for the reported failure: with AutoConfirm
        // off and no confirmer wired, this used to come back
        // ConfirmationRequired with an approvalId nothing can approve, and
        // the text was never typed.
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        var win = Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad");
        var (exec, input) = BuildExecutor(policy, win);

        var r = exec.Perform(TypeText());

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SendInput.type", r.Method);
        Assert.Equal(new[] { "Merhaba" }, input.Typed);
        Assert.DoesNotContain(r.Attempts, a => a.Method.StartsWith("confirm"));
    }

    [Fact]
    public void Executor_TypeText_IntoAgentLaunchedShell_StillDeniedByTheConsentGate()
    {
        // The relaxation must not become a shell bypass: the agent opened
        // pwsh itself, and typing into it is still arbitrary execution.
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(4242, "pwsh", 0x5555, "pwsh");
        var (exec, input) = BuildExecutor(policy, Win(0x5555, 4242, "PowerShell", "pwsh"));

        var r = exec.Perform(TypeText());

        Assert.False(r.Success);
        Assert.Equal(ErrorCode.ConfirmationRequired, r.Error);
        Assert.Contains("DangerousShellInput", r.ErrorMessage);
        Assert.Contains("LOCAL", r.ErrorMessage);
        Assert.Empty(input.Typed);
    }

    [Fact]
    public void Executor_TypeText_IntoUserOwnedWindow_ConfirmsAndNamesTheReason()
    {
        var (policy, prov) = Policy();
        prov.RegisterInitial(UserPid, "notepad.exe", UserHwnd);
        var (exec, input) = BuildExecutor(policy,
            Win(UserHwnd, UserPid, "Modelfile, Not Defteri", "notepad.exe"));

        var r = exec.Perform(TypeText());

        Assert.False(r.Success);
        Assert.Equal(ErrorCode.ConfirmationRequired, r.Error);
        Assert.Contains("UserOwnedWindowTextEntry", r.ErrorMessage);
        Assert.Contains("ownership: user", r.ErrorMessage);
        Assert.Empty(input.Typed);
        // the reason is machine-readable in the attempt trail too
        Assert.Contains(r.Attempts,
            a => a.Method == "policy:UserOwnedWindowTextEntry");
    }

    [Fact]
    public void Executor_TypeText_WithoutProvenance_ConfirmsAsUnknown()
    {
        var (policy, _) = Policy();
        var (exec, input) = BuildExecutor(policy,
            Win(UserHwnd, UserPid, "Modelfile, Not Defteri", "notepad.exe"));

        var r = exec.Perform(TypeText());

        Assert.False(r.Success);
        Assert.Equal(ErrorCode.ConfirmationRequired, r.Error);
        Assert.Contains("UnknownWindowTextEntry", r.ErrorMessage);
        Assert.Empty(input.Typed);
    }

    [Fact]
    public void Executor_KillSwitch_StillDeniesEverything_InAgentOwnedWindowToo()
    {
        var (policy, prov) = Policy();
        prov.RegisterAgentLaunch(AgentPid, "Notepad", AgentHwnd, "Notepad");
        policy.KillSwitch = true;
        var (exec, input) = BuildExecutor(policy, Win(AgentHwnd, AgentPid, "Not Defteri", "Notepad"));

        var r = exec.Perform(TypeText());

        Assert.False(r.Success);
        Assert.Equal(ErrorCode.PolicyDenied, r.Error);
        Assert.Contains("KillSwitch", r.ErrorMessage);
        Assert.Empty(input.Typed);
    }

    // ---------- capability contract ----------

    private static JsonElement Slim() =>
        JsonSerializer.Deserialize<JsonElement>(InbriskResources.CapabilitiesJsonSlim());

    private static JsonElement Full() =>
        JsonSerializer.Deserialize<JsonElement>(InbriskResources.CapabilitiesJson());

    [Fact]
    public void SlimProfile_PublishesWindowOwnership()
    {
        // The bug: ownership existed only under detail:"full", so a model
        // that never passed detail had no idea a pre-existing window was
        // off-limits. Policy must not depend on a verbosity flag.
        var root = Slim();

        Assert.True(root.TryGetProperty("ownership", out var own));
        var text = own.GetString()!;
        Assert.Contains("agent", text);
        Assert.Contains("user", text);
        Assert.Contains("do not close", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlimProfile_DoesNotShipTheWholeFullSchema()
    {
        // Guard against the ownership fix being "solved" by copying the
        // full document into the compact profile — that would triple the
        // token cost of the default call. Slim gets the summary; full
        // keeps the numbered rules.
        Assert.False(Slim().TryGetProperty("lifecycleRules", out _));
        Assert.True(Slim().GetProperty("usage").GetArrayLength() <= 6);
    }

    [Fact]
    public void SlimProfile_PublishesCandidatesWarning()
    {
        var root = Slim();
        Assert.True(root.TryGetProperty("ambiguity", out var amb));
        Assert.Contains("AmbiguousApplication", amb.GetString()!);
        Assert.Contains("candidates", amb.GetString()!);
    }

    [Fact]
    public void FullProfile_KeepsRule1ToRule6Shape()
    {
        // The agent side parses lifecyclePolicy as a rule1..rule6 dict
        // (lifecycle_policy_lines in ai_shell.py). The shape must not move.
        var policy = Full().GetProperty("lifecyclePolicy");
        Assert.Equal(JsonValueKind.Object, policy.ValueKind);
        for (var i = 1; i <= 6; i++)
            Assert.False(string.IsNullOrWhiteSpace(policy.GetProperty($"rule{i}").GetString()));
    }

    [Fact]
    public void BothProfiles_PublishTheSameOwnershipVerdict()
    {
        var slim = Slim().GetProperty("ownership").GetString()!;
        var full = Full().GetProperty("ownership").GetString()!;

        Assert.Equal(full, slim);
    }

    [Fact]
    public void BatchStepFields_AreDocumentedOnTheServer()
    {
        // This rule used to live only in the agent-side system prompt.
        // An unlisted field does not get ignored — the WHOLE batch is
        // rejected Malformed and nothing runs.
        var batch = Full().GetProperty("batch");
        var fields = batch.GetProperty("stepFields").GetString()!;

        foreach (var f in new[] { "do", "action", "t", "target", "v", "text",
                                  "value", "role", "hwnd", "ms", "waitFor",
                                  "submit", "keys", "continueOnError" })
            Assert.Contains(f, fields);

        Assert.Contains("REJECTED", batch.GetProperty("strictValidation").GetString()!);
    }

    [Fact]
    public void SlimProfile_PublishesBatchStepFields()
    {
        var text = Slim().GetProperty("batchStepFields").GetString()!;
        foreach (var f in new[] { "do", "t", "v", "ms", "submit", "keys" })
            Assert.Contains(f, text);
        Assert.Contains("Malformed", text);
    }

    [Fact]
    public void Usage_DoesNotContradictTheLaunchIdentifier()
    {
        // Old text: "prefer process over window title" (usage) next to
        // "app — friendly name — preferred" (launch.identifiers). The two
        // read as opposites. The rule is about WINDOW TARGETING; app: is an
        // application identity resolved via registration, not a title.
        const string legacy =
            "prefer elementIds over coordinates; prefer process over window title";

        foreach (var root in new[] { Slim(), Full() })
        {
            var usage = string.Join("\n", root.GetProperty("usage")
                .EnumerateArray().Select(u => u.GetString()!));
            Assert.DoesNotContain(legacy, usage, StringComparison.Ordinal);
            Assert.Contains("TARGETING a window", usage);
            Assert.Contains("preferred launch identifier", usage);
        }

        // the launch identifier stays preferred, and says what it is
        var identifiers = Full().GetProperty("launch").GetProperty("identifiers");
        var first = identifiers.EnumerateArray().First().GetString()!;
        Assert.Contains("app", first);
        Assert.Contains("APPLICATION name", first);
        Assert.Contains("not a window title", first);
    }

    [Fact]
    public void FullProfile_DocumentsLaunchOwnershipField()
    {
        var launch = Full().GetProperty("launch");
        var idempotent = launch.GetProperty("idempotent").GetString()!;

        Assert.Contains("ownership", idempotent);
        Assert.Contains("agent", idempotent);
        Assert.Contains("user", idempotent);
    }
}