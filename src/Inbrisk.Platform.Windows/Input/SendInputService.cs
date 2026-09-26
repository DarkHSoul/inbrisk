using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Input;

/// <summary>
/// Native input injection via SendInput. Coordinates are desktop-space
/// physical px; movement uses SetCursorPos (correct under PMv2) followed by
/// button events without a move flag.
///
/// Input ownership model — the anti-lockup invariant:
///   Every DOWN event is delivered through SendTracked, which ledger-marks it
///   ONLY when SendInput confirms delivery. Every composite op runs inside an
///   InputTransaction; on ANY exit path (success/exception/cancel/emergency)
///   owned input is released and reconciled against GetAsyncKeyState. A DOWN
///   can therefore never outlive the operation that created it — partial
///   SendInput delivery, UIPI rejection mid-sequence, cancellation between
///   DOWN and UP, watchdog timeout and panic all converge on the same owned-
///   release path.
///
/// Physical-vs-injected: every event carries our dwExtraInfo signature, and
/// hotkeys never press/release a modifier the USER is physically holding —
/// we only own what we inject.
/// </summary>
public sealed class SendInputService : IInputService
{
    private readonly object _heldGate = new();

    /// <summary>Ledger of delivered-but-unreleased DOWN events, keyed by
    /// (vk,scan) so unicode chars (WVk=0) stay distinct.</summary>
    private readonly Dictionary<(ushort Vk, ushort Scan), OwnedKey> _heldKeys = new();
    private readonly Dictionary<(ushort Vk, ushort Scan), DateTimeOffset> _heldKeySince = new();
    private readonly HashSet<uint> _heldButtons = new();
    private readonly Dictionary<uint, DateTimeOffset> _heldButtonSince = new();
    private readonly List<InputTransaction> _openTxns = new();

    /// <summary>Set by EmergencyCleanup: new DOWNs are rejected until
    /// ClearEmergency. Release paths bypass it so cleanup always works.</summary>
    private int _inputBlocked;

    // Modifier VKs swept unconditionally by SweepAll — covers input held by a
    // killed process instance whose tracking died with it.
    private static readonly ushort[] ModifierVks =
        { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
    private static readonly uint[] MouseDownFlags =
    {
        NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_RIGHTDOWN,
        NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_XDOWN,
    };
    private static readonly ushort[] ReconcileVks =
        { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C,
          0x0D, 0x1B, 0x09, 0x20 }; // Enter, Esc, Tab, Space

    /// <summary>dwExtraInfo signature on every event we inject — lets hooks /
    /// monitors distinguish Inbrisk input from physical user input.</summary>
    private static readonly IntPtr Marker =
        new(unchecked((int)(0x1B8A0000u | (uint)(Environment.ProcessId & 0xFFFF))));

    // Watchdog: a transaction must finish inside its budget or it is
    // force-released; a bare hold may not exceed BareHoldBudgetMs.
    private const int WatchdogPeriodMs = 200;
    private const int BareHoldBudgetMs = 30_000;
    private static readonly List<SendInputService> Live = new();
    private static int _exitHooked;
    private static Timer? _watchdog;
    private static int _watchdogStarted;

    public SendInputService()
    {
        Dpi.EnsurePerMonitorV2();
        lock (Live)
        {
            Live.Add(this);
            if (Interlocked.Exchange(ref _watchdogStarted, 1) == 0)
                _watchdog = new Timer(_ => WatchdogTick(), null,
                    WatchdogPeriodMs, WatchdogPeriodMs);
        }
        if (Interlocked.Exchange(ref _exitHooked, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                SendInputService[] instances;
                lock (Live) instances = Live.ToArray();
                foreach (var i in instances)
                    try { i.ReleaseAll(); } catch { /* exiting anyway */ }
            };
    }

    /// <summary>Snapshot of currently-held virtual keys (diagnostics/tests).</summary>
    public IReadOnlyCollection<ushort> HeldKeys
    { get { lock (_heldGate) return _heldKeys.Keys.Select(k => k.Vk).ToArray(); } }

    /// <summary>Number of open transactions (diagnostics/tests).</summary>
    public int OpenTransactions { get { lock (_heldGate) return _openTxns.Count; } }

    public (int X, int Y) CursorPosition()
    {
        NativeMethods.GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    // ------------------------------------------------------------------
    // transactions
    // ------------------------------------------------------------------

    /// <summary>Open an owned-input scope. All DOWN events sent through it are
    /// released on Dispose regardless of how the operation ends.</summary>
    public InputTransaction BeginTransaction(InputTxnKind kind, int? budgetMs = null)
    {
        var txn = new InputTransaction(this, kind, budgetMs ?? DefaultBudget(kind));
        lock (_heldGate) _openTxns.Add(txn);
        return txn;
    }

    private static int DefaultBudget(InputTxnKind kind) => kind switch
    {
        InputTxnKind.Type => 30_000,
        InputTxnKind.Drag => 10_000,
        InputTxnKind.Hold => BareHoldBudgetMs,
        _ => 2_000,
    };

    /// <summary>Test/diagnostic hook: send a single key inside a transaction's
    /// ownership scope.</summary>
    internal void TxnSendKey(InputTransaction txn, ushort vk, bool down) =>
        SendBatch(txn, new[] { Key(vk, down) }, allowWhenBlocked: !down);

    internal void EndTransaction(InputTransaction txn)
    {
        List<OwnedKey> keys; List<uint> buttons;
        lock (_heldGate)
        {
            _openTxns.Remove(txn);
            keys = txn.OwnedKeys.ToList(); txn.OwnedKeys.Clear();
            buttons = txn.OwnedButtons.ToList(); txn.OwnedButtons.Clear();
        }
        // post-action invariant: owned input still present at Dispose means
        // the operation's own pairing missed it (exception/cancel/partial
        // send) — the transaction net caught it. Normal ops end with zero.
        if (keys.Count + buttons.Count > 0)
            InputTelemetry.Record(new InputEventRecord
            {
                TxnId = txn.Id, Kind = txn.Kind, Phase = "invariant",
                Note = $"InputStateRecovered: {keys.Count} keys + " +
                    $"{buttons.Count} buttons owned at dispose",
            });
        // release in reverse press order — bypass the emergency block, this
        // IS the cleanup path
        for (var i = keys.Count - 1; i >= 0; i--)
            try { SendUp(keys[i], txn); } catch { }
        foreach (var flag in buttons)
            try { SendButtonUp(flag, txn); } catch { }
        ReconcileOwned(keys, buttons, txn);
    }

    // ------------------------------------------------------------------
    // release paths
    // ------------------------------------------------------------------

    /// <summary>Release every key/button we own. Idempotent — safe on any
    /// teardown path (cancel, exception, dispose, panic).</summary>
    public void ReleaseAll()
    {
        OwnedKey[] keys; uint[] buttons;
        lock (_heldGate)
        {
            keys = _heldKeys.Values.ToArray();
            buttons = _heldButtons.ToArray();
        }
        foreach (var k in keys)
            try { SendUp(k, null); } catch { /* teardown is best-effort */ }
        foreach (var flag in buttons)
            try { SendButtonUp(flag, null); } catch { }
    }

    /// <summary>
    /// Unconditional release sweep: every modifier and mouse button gets an UP
    /// whether or not we tracked it — clears input left held by a killed/older
    /// injector. Bypasses the emergency block (this is recovery).
    /// </summary>
    public void SweepAll()
    {
        var ups = new List<INPUT>(ModifierVks.Length + MouseDownFlags.Length + 16);
        foreach (var vk in ModifierVks) ups.Add(Key(vk, down: false));
        foreach (var flag in MouseDownFlags) ups.Add(Mouse(flag << 1));
        OwnedKey[] tracked;
        lock (_heldGate) tracked = _heldKeys.Values.ToArray();
        foreach (var k in tracked)
            if (k.Vk != 0 && Array.IndexOf(ModifierVks, k.Vk) < 0)
                ups.Add(Key(k.Vk, down: false));
        try { SendBatch(null, ups.ToArray(), allowWhenBlocked: true); }
        catch (InbriskException) { /* UIPI-blocked — try tracked release anyway */ }
        ReleaseAll();
    }

    /// <summary>
    /// Panic cleanup: block NEW injected input, give open transactions a short
    /// bounded window to unwind via their own release paths, then force-release
    /// everything still owned and reconcile against real keyboard/mouse state.
    /// Total worst-case cost is bounded (~drainMs + a few sends).
    /// </summary>
    public void EmergencyCleanup(int drainMs = 150)
    {
        Interlocked.Exchange(ref _inputBlocked, 1);
        var deadline = Environment.TickCount64 + drainMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_heldGate) if (_openTxns.Count == 0) break;
            Thread.Sleep(5);
        }
        ReleaseAll();
        ReconcileAll();
        InputTelemetry.Record(new InputEventRecord
        {
            Kind = InputTxnKind.Other, Phase = "release",
            Note = $"emergency cleanup; openTxns={OpenTransactions}; held={HeldKeys.Count}",
        });
    }

    /// <summary>Lift the emergency input block (local resume only).</summary>
    public void ClearEmergency() => Interlocked.Exchange(ref _inputBlocked, 0);

    /// <summary>Verify every owned key/button actually reads UP via
    /// GetAsyncKeyState; retry once, then report residue. Never touches input
    /// the ledger does not own — physical user keys are not ours to release.</summary>
    private void ReconcileOwned(List<OwnedKey> keys, List<uint> buttons,
        InputTransaction? txn)
    {
        foreach (var k in keys)
        {
            if (k.Vk == 0) continue; // unicode char — nothing queryable
            if ((NativeMethods.GetAsyncKeyState(k.Vk) & 0x8000) == 0) continue;
            Thread.Sleep(10); // let the queued UP land
            if ((NativeMethods.GetAsyncKeyState(k.Vk) & 0x8000) == 0) continue;
            InputTelemetry.Record(new InputEventRecord
            {
                TxnId = txn?.Id ?? "", Kind = txn?.Kind ?? InputTxnKind.Other,
                Phase = "reconcile", Vk = k.Vk, Note = "owned key still down after release — retrying",
            });
            try { SendUp(k, txn); } catch { }
            Thread.Sleep(10);
            if ((NativeMethods.GetAsyncKeyState(k.Vk) & 0x8000) != 0)
                InputTelemetry.Record(new InputEventRecord
                {
                    TxnId = txn?.Id ?? "", Kind = txn?.Kind ?? InputTxnKind.Other,
                    Phase = "reconcile-still-down", Vk = k.Vk,
                    Note = "key still down after retry — possibly held physically too",
                });
        }
        foreach (var flag in buttons)
        {
            var vk = flag switch
            {
                NativeMethods.MOUSEEVENTF_LEFTDOWN => 0x01,
                NativeMethods.MOUSEEVENTF_RIGHTDOWN => 0x02,
                NativeMethods.MOUSEEVENTF_MIDDLEDOWN => 0x04,
                _ => 0,
            };
            if (vk == 0 || (NativeMethods.GetAsyncKeyState(vk) & 0x8000) == 0) continue;
            Thread.Sleep(10);
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) == 0) continue;
            InputTelemetry.Record(new InputEventRecord
            {
                TxnId = txn?.Id ?? "", Kind = txn?.Kind ?? InputTxnKind.Other,
                Phase = "reconcile", MouseFlag = flag,
                Note = "owned mouse button still down after release — retrying",
            });
            try { SendButtonUp(flag, txn); } catch { }
        }
    }

    /// <summary>Reconcile everything the ledger owns — used by panic cleanup.</summary>
    private void ReconcileAll()
    {
        OwnedKey[] keys; uint[] buttons;
        lock (_heldGate)
        {
            keys = _heldKeys.Values.ToArray();
            buttons = _heldButtons.ToArray();
        }
        ReconcileOwned(keys.ToList(), buttons.ToList(), null);
    }

    // ------------------------------------------------------------------
    // diagnostics
    // ------------------------------------------------------------------

    /// <summary>Structured snapshot for computer_reset_input reporting.</summary>
    public IReadOnlyDictionary<string, object?> DiagnoseInput()
    {
        string[] keys; string[] buttons; List<Dictionary<string, object?>> txns;
        lock (_heldGate)
        {
            keys = _heldKeys.Values
                .Select(k => k.Vk != 0 ? $"0x{k.Vk:X2}" : $"U+{(int)k.Scan:X4}").ToArray();
            buttons = _heldButtons.Select(ButtonName).ToArray();
            txns = _openTxns.Select(t => new Dictionary<string, object?>
            {
                ["id"] = t.Id, ["kind"] = t.Kind.ToString(),
                ["ageMs"] = (long)(DateTimeOffset.UtcNow - t.StartedUtc).TotalMilliseconds,
                ["budgetMs"] = t.BudgetMs, ["abandoned"] = t.Abandoned,
            }).ToList();
        }
        var physical = new Dictionary<string, object?>();
        foreach (var vk in ReconcileVks)
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
                physical[$"0x{vk:X2}"] = "down";
        foreach (var (name, vk) in new[] { ("LButton", 0x01), ("RButton", 0x02),
            ("MButton", 0x04), ("XButton1", 0x05), ("XButton2", 0x06) })
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
                physical[name] = "down";
        return new Dictionary<string, object?>
        {
            ["ownedKeys"] = keys, ["ownedMouseButtons"] = buttons,
            ["physicalStateDown"] = physical, ["openTransactions"] = txns,
            ["inputBlocked"] = Volatile.Read(ref _inputBlocked) != 0,
            ["counters"] = new Dictionary<string, object?>
            {
                ["totalDown"] = Interlocked.Read(ref InputTelemetry.TotalDown),
                ["totalUp"] = Interlocked.Read(ref InputTelemetry.TotalUp),
                ["partialSends"] = Interlocked.Read(ref InputTelemetry.PartialSends),
                ["blockedSends"] = Interlocked.Read(ref InputTelemetry.BlockedSends),
                ["reconciles"] = Interlocked.Read(ref InputTelemetry.Reconciles),
                ["invariantRecoveries"] = Interlocked.Read(ref InputTelemetry.InvariantRecoveries),
                ["watchdogKills"] = Interlocked.Read(ref InputTelemetry.WatchdogKills),
            },
        };
    }

    // ------------------------------------------------------------------
    // public operations — every one runs inside a transaction
    // ------------------------------------------------------------------

    public void MoveMouse(int x, int y)
    {
        if (!NativeMethods.SetCursorPos(x, y))
            throw new InbriskException(ErrorCode.InputBlocked, "SetCursorPos failed");
    }

    public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1)
    {
        using var txn = BeginTransaction(InputTxnKind.Click);
        MoveMouse(x, y);
        var (down, up) = ButtonFlags(button);
        for (var i = 0; i < count; i++)
        {
            // down and up are SEPARATE sends: a failed send can therefore
            // never split a pair into an unowned held button
            SendBatch(txn, new[] { Mouse(down) });
            SendBatch(txn, new[] { Mouse(up) });
            if (i + 1 < count) Thread.Sleep(60);
        }
    }

    /// <summary>Press and hold a mouse button (ledger-tracked; watchdog
    /// releases holds older than 30s — a forgotten hold can never lock the
    /// desktop forever).</summary>
    public void MouseDown(MouseButton button = MouseButton.Left)
    {
        SendBatch(null, new[] { Mouse(ButtonFlags(button).Down) });
    }

    /// <summary>Release a held mouse button — bypasses the emergency block.</summary>
    public void MouseUp(MouseButton button = MouseButton.Left)
    {
        SendBatch(null, new[] { Mouse(ButtonFlags(button).Up) },
            allowWhenBlocked: true);
    }

    public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300,
        CancellationToken ct = default)
    {
        using var txn = BeginTransaction(InputTxnKind.Drag, durationMs + 2_000);
        ct.ThrowIfCancellationRequested();
        MoveMouse(fromX, fromY);
        SendBatch(txn, new[] { Mouse(NativeMethods.MOUSEEVENTF_LEFTDOWN) });
        try
        {
            var steps = Math.Max(4, durationMs / 16);
            for (var i = 1; i <= steps; i++)
            {
                ct.ThrowIfCancellationRequested();
                MoveMouse(fromX + (toX - fromX) * i / steps,
                          fromY + (toY - fromY) * i / steps);
                ct.WaitHandle.WaitOne(durationMs / steps);
            }
            ct.ThrowIfCancellationRequested();
            MoveMouse(toX, toY);
        }
        finally
        {
            try { SendBatch(txn, new[] { Mouse(NativeMethods.MOUSEEVENTF_LEFTUP) },
                allowWhenBlocked: true); } catch { /* txn release reconciles */ }
        }
    }

    public void Scroll(int x, int y, int wheelDelta)
    {
        using var txn = BeginTransaction(InputTxnKind.Scroll);
        MoveMouse(x, y);
        SendBatch(txn, new[] { new INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new INPUTUNION { Mi = new MOUSEINPUT { MouseData = (uint)wheelDelta, DwFlags = NativeMethods.MOUSEEVENTF_WHEEL } },
        } });
    }

    public void KeyPress(KeyCode key)
    {
        using var txn = BeginTransaction(InputTxnKind.Key);
        var vk = ToVk(key);
        SendBatch(txn, new[] { Key(vk, down: true) });
        SendBatch(txn, new[] { Key(vk, down: false) });
    }

    /// <summary>Bare hold primitive — ledger-tracked, watchdog-bounded.</summary>
    public void KeyDown(KeyCode key)
    {
        SendBatch(null, new[] { Key(ToVk(key), down: true) });
    }

    /// <summary>Release a held key — bypasses the emergency block.</summary>
    public void KeyUp(KeyCode key)
    {
        SendBatch(null, new[] { Key(ToVk(key), down: false) },
            allowWhenBlocked: true);
    }

    public void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key)
    {
        using var txn = BeginTransaction(InputTxnKind.Hotkey);
        var skipped = new List<ushort>();
        foreach (var m in modifiers)
        {
            var vk = ToVk(m);
            // Physical-interference guard: a modifier the USER (or any
            // non-Inbrisk source) already holds is not ours to press or
            // release — the hotkey still works, and their physical state
            // survives our action untouched.
            if (!LedgerOwns(vk) && PhysicallyDown(vk))
            {
                skipped.Add(vk);
                InputTelemetry.Record(new InputEventRecord
                {
                    TxnId = txn.Id, Kind = txn.Kind, Phase = "skip-physical",
                    Vk = vk, Note = "modifier already held externally — not owned, not released",
                });
                continue;
            }
            SendBatch(txn, new[] { Key(vk, down: true) });
        }
        try
        {
            var kv = ToVk(key);
            SendBatch(txn, new[] { Key(kv, down: true) });
            SendBatch(txn, new[] { Key(kv, down: false) });
        }
        finally
        {
            for (var i = modifiers.Count - 1; i >= 0; i--)
            {
                var vk = ToVk(modifiers[i]);
                if (skipped.Contains(vk)) continue; // never owned by us
                try
                {
                    SendBatch(txn, new[] { Key(vk, down: false) },
                        allowWhenBlocked: true);
                }
                catch { /* txn dispose reconciles */ }
            }
        }
    }

    /// <summary>Unicode text via KEYEVENTF_UNICODE — handles surrogate pairs
    /// and works regardless of keyboard layout. Small batches: a single huge
    /// burst can overrun the target's input queue and silently drop
    /// characters. Partial delivery marks every delivered DOWN as owned —
    /// the transaction releases them on unwind.</summary>
    public void TypeText(string text)
    {
        using var txn = BeginTransaction(InputTxnKind.Type,
            Math.Min(2_000 + text.Length * 50, 120_000));
        var inputs = new List<INPUT>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r') continue; // CRLF → single Enter
            if (ch == '\n')
            {
                inputs.Add(Key(0x0D, down: true));
                inputs.Add(Key(0x0D, down: false));
                continue;
            }
            inputs.Add(new INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                U = new INPUTUNION { Ki = new KEYBDINPUT { WScan = ch, DwFlags = NativeMethods.KEYEVENTF_UNICODE } },
            });
            inputs.Add(new INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                U = new INPUTUNION { Ki = new KEYBDINPUT { WScan = ch, DwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP } },
            });
        }
        const int batch = 16; // INPUT structs, ~8 characters
        for (var i = 0; i < inputs.Count; i += batch)
        {
            SendBatch(txn, inputs.Skip(i).Take(batch).ToArray());
            if (i + batch < inputs.Count) Thread.Sleep(15);
        }
    }

    // ------------------------------------------------------------------
    // send path — the ONLY place SendInput is called
    // ------------------------------------------------------------------

    private bool LedgerOwns(ushort vk)
    { lock (_heldGate) return _heldKeys.ContainsKey((vk, 0)); }

    private static bool PhysicallyDown(ushort vk)
        => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Deliver INPUTs and update the ledger to reflect EXACTLY what the OS
    /// accepted — the first <c>sent</c> events. Delivered DOWNs are marked
    /// held (and txn-owned) BEFORE any error propagates, so a partial send
    /// can never orphan a held key/button.
    /// </summary>
    private uint SendBatch(InputTransaction? txn, INPUT[] inputs,
        bool allowWhenBlocked = false)
    {
        if (Volatile.Read(ref _inputBlocked) != 0 && !allowWhenBlocked)
        {
            InputTelemetry.Record(new InputEventRecord
            {
                TxnId = txn?.Id ?? "", Kind = txn?.Kind ?? InputTxnKind.Other,
                Phase = "blocked", Requested = (uint)inputs.Length,
                Emergency = true, Note = "send rejected — emergency input block",
            });
            throw new InbriskException(ErrorCode.InputBlocked,
                "input blocked — emergency cleanup in progress");
        }
        for (var i = 0; i < inputs.Length; i++) StampMarker(ref inputs[i]);

        uint sent; int err = 0;
        lock (_heldGate)
        {
            sent = NativeMethods.SendInput((uint)inputs.Length, inputs,
                Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length) err = Marshal.GetLastWin32Error();
            for (var i = 0; i < sent; i++)
                ApplyDelivered(inputs[i], txn);
            for (var i = (int)sent; i < inputs.Length; i++)
                InputTelemetry.Record(EventFor(inputs[i], txn,
                    phase: "undelivered", requested: (uint)inputs.Length,
                    delivered: sent, lastError: err));
        }
        if (sent != inputs.Length)
        {
            InputTelemetry.Record(new InputEventRecord
            {
                TxnId = txn?.Id ?? "", Kind = txn?.Kind ?? InputTxnKind.Other,
                Phase = "partial", Requested = (uint)inputs.Length,
                Delivered = sent, LastError = err,
            });
            throw new InbriskException(ErrorCode.InputBlocked,
                $"SendInput delivered {sent}/{inputs.Length} events — " +
                "likely blocked by a higher-integrity-level foreground window (UIPI)");
        }
        return sent;
    }

    /// <summary>Ledger bookkeeping for one DELIVERED event.</summary>
    private void ApplyDelivered(in INPUT e, InputTransaction? txn)
    {
        InputTelemetry.Record(EventFor(e, txn,
            phase: EventPhase(e), requested: 1, delivered: 1, lastError: 0));
        if (e.Type == NativeMethods.INPUT_KEYBOARD)
        {
            var k = new OwnedKey(e.U.Ki.WVk, e.U.Ki.WScan,
                e.U.Ki.DwFlags & NativeMethods.KEYEVENTF_UNICODE);
            var id = (k.Vk, k.Scan);
            if (EventPhase(e) == "up")
            {
                _heldKeys.Remove(id); _heldKeySince.Remove(id);
                txn?.OwnedKeys.Remove(k);
            }
            else
            {
                _heldKeys[id] = k; _heldKeySince[id] = DateTimeOffset.UtcNow;
                txn?.OwnedKeys.Add(k);
            }
        }
        else if (e.Type == NativeMethods.INPUT_MOUSE)
        {
            var f = e.U.Mi.DwFlags;
            if ((f & (NativeMethods.MOUSEEVENTF_LEFTUP | NativeMethods.MOUSEEVENTF_RIGHTUP |
                      NativeMethods.MOUSEEVENTF_MIDDLEUP | NativeMethods.MOUSEEVENTF_XUP)) != 0)
            {
                var down = f >> 1; // UP flags are DOWN flags << 1
                _heldButtons.Remove(down); _heldButtonSince.Remove(down);
                txn?.OwnedButtons.Remove(down);
            }
            else if ((f & (NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_RIGHTDOWN |
                           NativeMethods.MOUSEEVENTF_MIDDLEDOWN | NativeMethods.MOUSEEVENTF_XDOWN)) != 0)
            {
                _heldButtons.Add(f); _heldButtonSince[f] = DateTimeOffset.UtcNow;
                txn?.OwnedButtons.Add(f);
            }
        }
    }

    private static string EventPhase(in INPUT e)
    {
        if (e.Type == NativeMethods.INPUT_KEYBOARD)
            return (e.U.Ki.DwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0 ? "up" : "down";
        if (e.Type == NativeMethods.INPUT_MOUSE)
        {
            var f = e.U.Mi.DwFlags;
            if ((f & (NativeMethods.MOUSEEVENTF_LEFTUP | NativeMethods.MOUSEEVENTF_RIGHTUP |
                      NativeMethods.MOUSEEVENTF_MIDDLEUP | NativeMethods.MOUSEEVENTF_XUP)) != 0)
                return "up";
            if ((f & (NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_RIGHTDOWN |
                      NativeMethods.MOUSEEVENTF_MIDDLEDOWN | NativeMethods.MOUSEEVENTF_XDOWN)) != 0)
                return "down";
        }
        return "event"; // move / wheel — never held
    }

    private InputEventRecord EventFor(in INPUT e, InputTransaction? txn,
        string phase, uint requested, uint delivered, int lastError) => new()
    {
        TxnId = txn?.Id ?? "", Kind = txn?.Kind ?? InputTxnKind.Other,
        Phase = phase,
        Vk = e.Type == NativeMethods.INPUT_KEYBOARD ? e.U.Ki.WVk : null,
        Scan = e.Type == NativeMethods.INPUT_KEYBOARD ? e.U.Ki.WScan : null,
        MouseFlag = e.Type == NativeMethods.INPUT_MOUSE ? e.U.Mi.DwFlags : null,
        Requested = requested, Delivered = delivered, LastError = lastError,
        Emergency = Volatile.Read(ref _inputBlocked) != 0,
    };

    private static void StampMarker(ref INPUT e)
    {
        if (e.Type == NativeMethods.INPUT_KEYBOARD) e.U.Ki.DwExtraInfo = Marker;
        else if (e.Type == NativeMethods.INPUT_MOUSE) e.U.Mi.DwExtraInfo = Marker;
    }

    /// <summary>Send a faithful UP for an owned key — unicode-safe, bypasses
    /// the emergency block.</summary>
    private void SendUp(OwnedKey k, InputTransaction? txn)
    {
        INPUT up = (k.Flags & NativeMethods.KEYEVENTF_UNICODE) != 0
            ? new INPUT { Type = NativeMethods.INPUT_KEYBOARD, U = new INPUTUNION
                { Ki = new KEYBDINPUT { WScan = k.Scan,
                    DwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP } } }
            : Key(k.Vk, down: false);
        SendBatch(txn, new[] { up }, allowWhenBlocked: true);
        InputTelemetry.Record(new InputEventRecord
        {
            TxnId = txn?.Id ?? "", Kind = txn?.Kind ?? InputTxnKind.Other,
            Phase = "release", Vk = k.Vk, Scan = k.Scan,
        });
    }

    private void SendButtonUp(uint downFlag, InputTransaction? txn)
    {
        SendBatch(txn, new[] { Mouse(downFlag << 1) }, allowWhenBlocked: true);
        InputTelemetry.Record(new InputEventRecord
        {
            TxnId = txn?.Id ?? "", Kind = txn?.Kind ?? InputTxnKind.Other,
            Phase = "release", MouseFlag = downFlag << 1,
        });
    }

    // ------------------------------------------------------------------
    // watchdog
    // ------------------------------------------------------------------

    private static void WatchdogTick()
    {
        SendInputService[] instances;
        lock (Live) instances = Live.ToArray();
        foreach (var svc in instances)
            try { svc.WatchdogScan(); } catch { /* watchdog must never die */ }
    }

    private void WatchdogScan()
    {
        var now = DateTimeOffset.UtcNow;
        InputTransaction[] txns;
        KeyValuePair<(ushort Vk, ushort Scan), DateTimeOffset>[] bareKeys;
        KeyValuePair<uint, DateTimeOffset>[] bareButtons;
        lock (_heldGate)
        {
            txns = _openTxns.ToArray();
            bareKeys = _heldKeySince.ToArray();
            bareButtons = _heldButtonSince.ToArray();
        }
        // over-budget transactions → abandon + force release owned
        foreach (var txn in txns)
        {
            if ((now - txn.StartedUtc).TotalMilliseconds <= txn.BudgetMs) continue;
            txn.Abandon();
            InputTelemetry.Record(new InputEventRecord
            {
                TxnId = txn.Id, Kind = txn.Kind, Phase = "watchdog",
                Note = $"transaction exceeded budget ({txn.BudgetMs}ms) — force releasing",
            });
            List<OwnedKey> keys; List<uint> buttons;
            lock (_heldGate)
            {
                _openTxns.Remove(txn);
                keys = txn.OwnedKeys.ToList(); txn.OwnedKeys.Clear();
                buttons = txn.OwnedButtons.ToList(); txn.OwnedButtons.Clear();
            }
            foreach (var k in keys)
                try { SendUp(k, txn); } catch { }
            foreach (var f in buttons)
                try { SendButtonUp(f, txn); } catch { }
            ReconcileOwned(keys, buttons, txn);
        }
        // bare holds beyond the budget → auto-release (a forgotten hold can
        // never strand the desktop)
        foreach (var (id, since) in bareKeys)
        {
            if ((now - since).TotalMilliseconds <= BareHoldBudgetMs) continue;
            OwnedKey k;
            lock (_heldGate)
                if (!_heldKeys.TryGetValue(id, out k)) continue;
            InputTelemetry.Record(new InputEventRecord
            {
                Kind = InputTxnKind.Hold, Phase = "watchdog", Vk = id.Vk,
                Note = $"bare hold exceeded {BareHoldBudgetMs}ms — auto-releasing",
            });
            try { SendUp(k, null); } catch { }
        }
        foreach (var (flag, since) in bareButtons)
        {
            if ((now - since).TotalMilliseconds <= BareHoldBudgetMs) continue;
            InputTelemetry.Record(new InputEventRecord
            {
                Kind = InputTxnKind.Hold, Phase = "watchdog", MouseFlag = flag,
                Note = $"bare button hold exceeded {BareHoldBudgetMs}ms — auto-releasing",
            });
            try { SendButtonUp(flag, null); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static (uint Down, uint Up) ButtonFlags(MouseButton b) => b switch
    {
        MouseButton.Right => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
        MouseButton.Middle => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
        _ => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP),
    };

    private static string ButtonName(uint downFlag) => downFlag switch
    {
        NativeMethods.MOUSEEVENTF_LEFTDOWN => "Left",
        NativeMethods.MOUSEEVENTF_RIGHTDOWN => "Right",
        NativeMethods.MOUSEEVENTF_MIDDLEDOWN => "Middle",
        NativeMethods.MOUSEEVENTF_XDOWN => "X",
        _ => $"0x{downFlag:X}",
    };

    private static INPUT Mouse(uint flags) => new()
    {
        Type = NativeMethods.INPUT_MOUSE,
        U = new INPUTUNION { Mi = new MOUSEINPUT { DwFlags = flags } },
    };

    private static INPUT Key(ushort vk, bool down) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        U = new INPUTUNION { Ki = new KEYBDINPUT { WVk = vk, DwFlags = down ? 0 : NativeMethods.KEYEVENTF_KEYUP } },
    };

    private static ushort ToVk(KeyCode k) => k switch
    {
        KeyCode.Enter => 0x0D, KeyCode.Escape => 0x1B, KeyCode.Tab => 0x09,
        KeyCode.Space => 0x20, KeyCode.Backspace => 0x08, KeyCode.Delete => 0x2E,
        KeyCode.Left => 0x25, KeyCode.Up => 0x26, KeyCode.Right => 0x27, KeyCode.Down => 0x28,
        KeyCode.Home => 0x24, KeyCode.End => 0x23, KeyCode.PageUp => 0x21, KeyCode.PageDown => 0x22,
        KeyCode.Ctrl => 0xA2, KeyCode.Shift => 0xA0, KeyCode.Alt => 0xA4, KeyCode.Win => 0x5B,
        KeyCode.Pause => 0x13,
        KeyCode.F1 => 0x70, KeyCode.F2 => 0x71, KeyCode.F3 => 0x72, KeyCode.F4 => 0x73,
        KeyCode.F5 => 0x74, KeyCode.F6 => 0x75, KeyCode.F7 => 0x76, KeyCode.F8 => 0x77,
        KeyCode.F9 => 0x78, KeyCode.F10 => 0x79, KeyCode.F11 => 0x7A, KeyCode.F12 => 0x7B,
        >= KeyCode.A and <= KeyCode.Z => (ushort)(0x41 + (int)k - (int)KeyCode.A),
        >= KeyCode.D0 and <= KeyCode.D9 => (ushort)(0x30 + (int)k - (int)KeyCode.D0),
        _ => throw new InbriskException(ErrorCode.Unsupported, $"no VK mapping for {k}"),
    };
}
