# Inbrisk — Architecture Assessment & Implementation Plan

Assessment of `todo.txt` (the product spec). This document answers the 15
questions in §30 and is the working foundation for the codebase. It is
deliberately opinionated: where the spec and this document disagree, this
document wins until revised.

---

## 0. Verdict

The architecture is fundamentally correct. The hybrid "UIA → specialist →
vision" model, the capture/perception separation, element identity, and
observe→act→verify are all the right calls and match how serious
computer-use systems are built.

The spec's real risks are not conceptual — they are Windows-specific
engineering realities it under-specifies:

1. **UIA tree scale.** Browsers and Electron apps expose their entire DOM
   through UIA — a single Chrome window can produce 10k+ elements. Without
   aggressive pruning the "semantic tree" is unusable.
2. **No event system.** The spec relies on frame-diff for change detection,
   but Windows already has *semantic* change signals — `SetWinEventHook`
   and UIA event handlers — that are cheaper and more precise than pixel
   diffing. This is the largest missing subsystem.
3. **Integrity levels / secure desktop.** SendInput to elevated windows is
   *silently dropped*; the UAC consent screen lives on a separate desktop
   that cannot be observed or clicked at all. This must be detected and
   reported, not discovered as a silent failure.
4. **Foreground-lock.** `SetForegroundWindow` frequently fails by design.
   Clicking into background windows requires an explicit focus strategy.
5. **Coordinate bookkeeping.** The DPI problem is correctly identified but
   the real bug source is subtler: every frame must carry its own
   transform (crop origin + scale) because models return coordinates in
   *image* space, not desktop space.

---

## 1. Implementation language: **C# / .NET 8**

Decision: the Windows-native core is **C# on .NET 8**.

Rationale:

- **UIA is a COM API.** .NET has the cleanest COM interop available.
  FlaUI (MIT) is proof that the entire client-side UIA surface is
  practical from C#, and its source is a reference implementation.
- **CsWin32** gives source-generated P/Invoke for everything Win32
  (SendInput, EnumWindows, SetWinEventHook, monitor APIs, DPI) — zero
  hand-written interop boilerplate.
- **WinRT is first-class** via CsWinRT: `Windows.Graphics.Capture` and
  `Windows.Media.Ocr` later.
- **Iteration speed.** This project's cost is API-surface coverage and
  correctness, not raw throughput — the control loop is LLM-latency-bound.
  C# iterates measurably faster than Rust on COM-heavy work (UIA event
  sinks and marshaling in `windows-rs` are workable but slow going).
- **Distribution is fine:** single-file self-contained `inbrisk.exe`.
- **MCP later:** official C# MCP SDK exists; the wrapper will be thin.

Honest alternative: **Rust** (`windows-rs`) — smaller binary, embeddable
as a dylib for future SDKs. Rejected for now because iteration velocity
on UIA/COM matters more than binary minimalism at this stage. If the core
is later needed as an embeddable native library for other languages,
revisit.

Rejected: **Python** for the core (fragile UIA stack, packaging,
distribution, perf for capture) and **C++** (velocity).

## 2. Native vs. higher-level split

- **In-process native core (C#):** everything that touches Windows truth —
  capture, UIA, input injection, window/process/monitor management,
  events, coordinate mapping, element registry, action resolution,
  verification, safety policy, telemetry.
- **Core is a library + thin hosts.** `Inbrisk.Sdk` is the public API;
  `Inbrisk.Cli` hosts it for humans; later `Inbrisk.Mcp` and a JSON-RPC
  server host it for agents. The core must never assume who its caller is.
- **Agent layer (later, any language):** model adapters and the
  observe→act loop may live in Python/TypeScript and drive the core over
  JSON-RPC. Milestone 1 keeps everything in one C# process — no IPC yet.

## 3. Critical corrections to the spec

| # | Spec claim / gap | Reality | Design consequence |
|---|---|---|---|
| 1 | UIA gives a usable semantic tree | Browser/Electron UIA trees are enormous (DOM mirrored, 10k+ nodes) | Mandatory pruning pipeline: offscreen cull, depth cap, control-type filter, dedup; lazy element resolution |
| 2 | Frame-diff is the change signal | `SetWinEventHook` + UIA events give semantic signals (foreground, create/destroy, name/value change) cheaper and more precise | Events become a first-class subsystem; frame-diff is the fallback for pixel-only surfaces |
| 3 | 30/60 FPS continuous capture | Wasteful even locally at 4K | Adaptive ~10–15 FPS on downscaled grayscale; escalate rate only around detected change |
| 4 | SendInput "just works" | Silently dropped against higher-integrity-level windows (UIPI) | Detect target integrity level; report `TargetElevated` instead of silent no-op |
| 5 | Everything is observable | UAC secure desktop is a separate input desktop: unobservable, unclickable | Detect via `OpenInputDesktop`; report `SecureDesktopActive` |
| 6 | `focus_window()` is trivial | Foreground-lock denies `SetForegroundWindow` routinely | Foreground policy: `AttachThreadInput` + `BringWindowToTop`, or input-simulated click to earn foreground rights |
| 7 | Vision → coordinates → click | Models answer in *image* space; the image was cropped+downscaled | Every frame carries a `FrameTransform`; `CoordinateMapper` is the single conversion point |
| 8 | UIA calls are safe to run inline | Cross-process UIA calls block indefinitely if the target hangs | All UIA work on a dedicated MTA thread with timeouts; never on the orchestration thread |
| 9 | Element IDs are enough | UIA elements die (UIA_E_ELEMENTNOTAVAILABLE); RuntimeIds are unstable | Each element stores a re-resolution recipe (name/role/automationId/ancestry/bounds/pid); stale → re-resolve → retry once |
| 10 | CDP usable on any browser | Requires `--remote-debugging-port` at launch; cannot attach to arbitrary Chrome | CDP is opportunistic: detect debug port, else fall back to UIA+vision for that window |
| 11 | Capture always returns pixels | DRM/protected/exclusive-fullscreen surfaces render black under WGC; GDI has its own gaps | Detect black/protected frames; report capture capability per window |
| 12 | Verification is a check | Many actions have no observable state transition | Pluggable verifiers (state re-read, event observed, region changed/stable); `Unverified` is an honest result |

## 4. Missing Windows technologies to add

| Technology | Why it matters |
|---|---|
| `SetWinEventHook` (WinEvents) | Cheap semantic signals: foreground change, window create/destroy/show, name/value changes. Powers `wait_for_*` without polling pixels |
| UIA event handlers | `StructureChanged`, `PropertyChanged`, `FocusChanged`, `AutomationEvent` — invalidate element cache, detect UI mutation |
| `IVirtualDesktopManager` | `EnumWindows` lists windows on *other* virtual desktops; they are not clickable. Filter or annotate them |
| `Windows.Media.Ocr` (WinRT) | Built-in local OCR — zero dependency, satisfies the OCR backend |
| Integrity level check | `OpenProcessToken`/`GetTokenInformation` on the target PID → know *before* input whether UIPI will drop it |
| `OpenInputDesktop` | Detects secure desktop (UAC, lock screen, Ctrl+Alt+Del) — the only correct answer there is "cannot act" |
| `AttachThreadInput` + `BringWindowToTop` | Reliable foreground strategy around foreground-lock |
| `MOUSEEVENTF_VIRTUALDESK` | Correct SendInput absolute-coordinate mapping across multi-monitor virtual space (0–65535 normalized) |
| `PrintWindow` (PW_RENDERFULLCONTENT) | Capture fallback for occluded/minimized windows where WGC/GDI fail |
| MSAA / IAccessible2 | Mostly bridged by UIA already — noted so nobody builds a redundant backend |

## 5. Module / package architecture

```
inbrisk.sln
├── src/
│   ├── Inbrisk.Core/               Domain model — no Windows calls
│   │     UiElement, Observation, Rect, MonitorTopology, FrameTransform,
│   │     ActionIntent, ActionResult, Role, BackendId, ElementHandle,
│   │     VerifySpec, policy types, telemetry records
│   │
│   ├── Inbrisk.Platform.Windows/   All native interop (CsWin32 + UIA COM + WinRT)
│   │     Capture/    WGC primary, GDI/PrintWindow fallback, continuous
│   │                   stream, frame-diff, stability detector
│   │     Uia/        tree walk + pruning, find, patterns (Invoke, Value,
│   │                   Toggle, Selection, ExpandCollapse, RangeValue),
│   │                   UIA events, element cache
│   │     Input/      SendInput mouse/keyboard, Unicode text, clipboard
│   │     Windows/    EnumWindows, processes, monitors, DPI,
│   │                   virtual desktops, integrity level, foreground
│   │     Events/     WinEvents + UIA events → internal event bus
│   │
│   ├── Inbrisk.Runtime/            The runtime brain (backend-agnostic)
│   │     SceneMerger    UIA + vision + OCR → unified element list,
│   │                    spatial dedup, runtime ID assignment
│   │     Resolver       action → strongest backend strategy
│   │     Executor       perform + retry + fallback chain
│   │     Verifier       pluggable verification strategies
│   │     Safety         ALLOW/CONFIRM/DENY policy, rate limits, kill switch
│   │     Telemetry      structured per-action records
│   │
│   ├── Inbrisk.Sdk/                Public facade — the "Unified Computer API"
│   │
│   ├── Inbrisk.Cli/                inbrisk.exe — human-facing commands
│   │
│   ├── (later) Inbrisk.Agent/      observe→act loop + model adapters
│   ├── (later) Inbrisk.Mcp/        thin MCP wrapper over Sdk
│   └── (later) Inbrisk.Server/     JSON-RPC host for external agents
│
└── tests/
    ├── Inbrisk.TestApp/            Deterministic WPF fixture app —
    │                               known controls + AutomationIds
    └── Inbrisk.Tests/              xunit end-to-end driver tests
```

Dependency rule: `Core` depends on nothing; `Platform.Windows` depends on
`Core`; `Runtime` depends on both; `Sdk` composes; hosts depend on `Sdk`.
Nothing inside `Platform.Windows` may leak COM/Win32 types upward — the
boundary types are the domain records.

## 6. Unified element / observation model

```csharp
// Canonical coordinates: physical pixels in virtual-desktop space
// (origin may be negative; covers all monitors).

public sealed record RectPx(int X, int Y, int Width, int Height);

public enum BackendId { Uia, Cdp, Vision, Ocr, Win32 }
public enum Role { Window, Pane, Button, Edit, Document, Menu, MenuItem,
    Tab, List, ListItem, Tree, TreeItem, ComboBox, CheckBox, RadioButton,
    Slider, Toggle, Toolbar, Dialog, Text, Unknown }

public sealed record ElementHandle(
    BackendId Backend,
    string BackendRef,          // opaque: UIA runtime path, CDP node id…
    ReResolveRecipe Recipe);    // name/role/automationId/ancestry/bounds/pid

public sealed record UiElement(
    string Id,                  // runtime id: "uia_41", "vision_7"
    BackendId Source,
    Role Role,
    string? Name,
    RectPx Bounds,
    IReadOnlyList<string> Actions,          // "invoke","setvalue","toggle","click"…
    IReadOnlyDictionary<string, object?> Props, // enabled/focused/value/state…
    ElementHandle Handle,
    int? Pid, long? Hwnd,
    double? Confidence);        // vision/ocr only

public sealed record FrameTransform(
    RectPx SourceRect,          // desktop-space rect the image covers
    int ImageWidth, int ImageHeight); // actual pixels sent to the model

public sealed record Observation(
    WindowInfo ActiveWindow,
    IReadOnlyList<WindowInfo> Windows,
    IReadOnlyList<UiElement> Elements,     // already pruned
    FrameTransform? Frame,                 // if a screenshot accompanied it
    IReadOnlyList<ObservedEvent> Events,   // semantic events since last obs
    DateTimeOffset At);
```

Rule: a model never sees raw desktop coordinates. It sees image-relative
coordinates; `CoordinateMapper` (the only conversion point) maps them back
through the `FrameTransform` of the frame that produced them.

## 7. Action interface

```csharp
public sealed record ActionIntent(
    ActionKind Kind,        // Click, RightClick, DoubleClick, Drag, Scroll,
                            // Invoke, SetValue, Toggle, Select, Expand,
                            // TypeText, Key, Hotkey, FocusWindow, WaitFor…,
                            // ClipboardRead/Write
    TargetRef Target,       // elementId | selector | point | windowRef
    IReadOnlyDictionary<string, object?> Args,
    VerifySpec? Verify,     // expected postcondition, if any
    SafetyClass Safety);    // assigned by policy layer, not the caller

public sealed record ActionResult(
    bool Success,
    BackendId BackendUsed,
    string Method,                     // "UIA.InvokePattern", "SendInput.click"
    IReadOnlyList<Attempt> Attempts,   // fallback chain actually taken
    VerifyResult Verification,         // Verified | Unverified | Failed
    TimeSpan Duration);
```

`PerformAsync(ActionIntent)` is the single entry point. Callers express
intent; the runtime picks the backend. Advanced callers may pin a backend
for debugging.

## 8. Backend selection / fallback

Resolution order for a **locate** request, evaluated per target window:

```
1. UIA semantic find          (structural truth)
2. CDP DOM find               (only if target is Chromium with debug port)
3. OCR text locate            (cheap, for "find text X")
4. Vision grounding           (expensive; last resort, pixel surfaces)
```

Resolution for an **act** request on a resolved element:

```
1. Native semantic action     (element.Actions advertises "invoke" → InvokePattern)
2. Specialist action          (CDP input for Chromium content)
3. SendInput on bounds center (universal fallback; preceded by foreground guard)
```

Every fallback transition is recorded in `Attempts[]` — silent fallback is
a debugging nightmare. Backend priority is policy-configurable, but the
default ordering above ships.

Important refinement of the spec's linear fallback: selection is
**per-window capability detection**, not a global ladder. A Chrome window
yields UIA for its chrome (menus, tabs, dialogs) and CDP for its DOM
content simultaneously; the scene merger owns which backend claims which
region.

## 9. DPI & multi-monitor coordinate model

- Process sets `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2` at startup.
- **One canonical space:** physical pixels in the virtual desktop
  (`SM_XVIRTUALSCREEN` bounds; origin may be negative). All `RectPx`
  everywhere are in this space.
- `MonitorTopology` from `EnumDisplayMonitors` + `GetDpiForMonitor`:
  `{rect, dpiScale, isPrimary, refreshRate}` per monitor.
- Every captured frame is stamped with a `FrameTransform`. Model-facing
  images may be downscaled to model-optimal width (~1366px); the
  transform preserves the mapping.
- `CoordinateMapper` is the *only* code allowed to convert between:
  image space ↔ desktop space ↔ SendInput normalized space (0–65535,
  `MOUSEEVENTF_VIRTUALDESK`).
- Mouse implementation: `SetCursorPos` (physical px, correct under PMv2)
  followed by button events *without* a move flag — simpler and more
  reliable than computing normalized absolute input.

## 10. Stale element strategy

- Every `UiElement` carries a `ReResolveRecipe`: `{pid, hwnd, role, name,
  automationId, ancestry-path, last bounds}`.
- On use: attempt direct backend ref → on `UIA_E_ELEMENTNOTAVAILABLE`
  (or equivalent), run re-resolution via recipe → refresh bounds → retry
  once → then report `StaleUnresolvable`.
- Element cache is invalidated by `StructureChanged`/destroy WinEvents,
  not just TTL.
- `element.is_stale()` is a public SDK call for callers that hold ids.

## 11. Security & integrity-level limitations

- **UIPI:** a medium-integrity process cannot inject input into elevated
  windows — SendInput is dropped *without error*. Before acting, compare
  target process integrity level; report `TargetElevated` with the
  remediation (run inbrisk elevated / `--elevated` helper host).
- **Secure desktop:** UAC consent, lock screen, Ctrl+Alt+Del live on a
  separate input desktop. Detect via `OpenInputDesktop` name check;
  answer is always `SecureDesktopActive` — no capture, no input.
- **UIA across integrity levels:** partially denied; detect and annotate
  rather than emit a mysteriously empty tree.
- **`uiAccess`** manifest flag could bypass some of this but requires
  Authenticode signing + a secure install location — noted, deferred.
- **Injected-input flag:** input carries `LLKHF_INJECTED`; anti-cheat and
  some games reject it — documented limitation.
- **Password fields:** UIA `IsPassword=true` → never expose `Value`;
  safety policy classifies credential entry as `CONFIRM` minimum.
- Policy engine (`ALLOW/CONFIRM/DENY`) sits in `Runtime.Safety`, applied
  to every `ActionIntent` before resolution — never inside backends.

## 12. Observability

Every action emits a structured record (the spec's example format is the
right shape): intent, resolved element, backend, method, attempts chain,
duration, verification outcome, before/after observation deltas.
Telemetry is a `Runtime` service writing JSONL locally; the CLI can
`inbrisk log tail` it. Screenshot frames referenced by hash, stored on
demand.

## 13. Milestone 1 — driver-only, no LLM

Prove Inbrisk can understand and manipulate Windows *without* a model.
CLI surface:

```
inbrisk windows                          list top-level windows (title, pid,
                                         hwnd, bounds, state, monitor, elevated)
inbrisk inspect <window>                 pruned UIA tree → stdout
inbrisk find --window <w> --role button --name "Save"
inbrisk invoke <id> | click <id> | setvalue <id> "text" | type "text"
inbrisk observe [--window <w>] [--shot]  unified JSON observation
inbrisk shot [--window <w>] out.png      single-frame capture
inbrisk wait stable|element <id>         event-driven waits
inbrisk focus <window>
```

### Automated tests (xunit, against `Inbrisk.TestApp`)

`TestApp` is a small WPF fixture shipped in the test tree — deterministic
controls with known `AutomationId`s, a button that flips a verifiable
flag, a textbox, a checkbox, a disabled control. Using it instead of
Notepad/Calc keeps tests stable across Windows builds.

- **Discovery:** `windows` lists TestApp with correct pid/bounds/state.
- **Find:** role+name query returns exactly the expected element with
  correct bounds (physical px).
- **Invoke:** `invoke(button)` flips TestApp's flag — *verified by reading
  app state*, not by API success.
- **SetValue/type:** textbox round-trips typed Unicode text.
- **Toggle:** checkbox state reads `On` after toggle.
- **Disabled control:** invoke on disabled button reports honest failure.
- **Stale element:** close+reopen a dialog → old id reports stale,
  re-resolution recovers.
- **CoordinateMapper:** pure unit tests over synthetic transforms
  (downscale, crop offset, negative monitor origin, 125%/150% scale).
- **Capture:** frame non-empty, dimensions match monitor topology.
- **Foreground:** focus_window brings TestApp foregrounded and a
  subsequent click lands.

Milestone exit: the CLI reliably drives TestApp *and* Notepad *and*
`inspect` on Settings produces a sane pruned tree — all with honest
verification reporting.

## 14. Backend cooperation contract

Each backend owns exactly one kind of truth; overlap is resolved by the
scene merger, never by duplicated logic:

| Concern | Owner | Never owned by |
|---|---|---|
| Window/process/monitor topology | Win32 (`Platform.Windows`) | UIA, vision |
| Semantic UI tree | UIA | OCR, vision |
| DOM content (Chromium) | CDP | UIA tree walking into DOM |
| Pixels | Capture engine | UIA (bounding rects are metadata, not pixels) |
| Text inside pixels | OCR | vision (unless grounding) |
| Pixel-space grounding | Vision | UIA |
| Input injection | Input | backends never inject input directly |
| Change signals | Events + frame-diff | model polling |

Spatial dedup rule: when a vision/OCR element overlaps a UIA element's
bounds, the UIA element wins and the other is dropped (semantic truth >
pixel inference).

## 15. Changes to the proposal (delta list)

1. **Add an Events subsystem** (WinEvents + UIA events) at milestone-1
   priority — it powers `wait_for_*` and cache invalidation more reliably
   than frame-diff.
2. **Reframe backend fallback** as per-window capability detection rather
   than a single global ladder (§8).
3. **Add `Inbrisk.TestApp`** as a first-class deliverable — deterministic
   testing is impossible against system apps alone.
4. **Adaptive capture rate** (~10–15 FPS diff baseline) instead of fixed
   30/60 FPS.
5. **Foreground/elevation/secure-desktop guards** are part of the action
   pipeline from day one, not edge cases discovered later.
6. **`FrameTransform` bookkeeping** is mandatory on every observation —
   this is the correction that prevents the entire class of
   coordinate-drift bugs.
7. **Verification result includes `Unverified`** — honesty over false
   confidence.
8. **Element pruning is a pipeline**, specified now: offscreen cull →
   depth cap → control-type filter → dedup → relevance ordering.
9. **UIA on a dedicated thread with timeouts** — architectural, not an
   implementation detail.
10. **CDP marked opportunistic** (requires debug port at launch).
```

---

## What happens next

Build order (Events moved into M1 — it powers `wait_for_*` and element-cache
invalidation, so it belongs to the foundation):

```
M1: windows/process/monitor topology
    → DPI + CoordinateMapper + FrameTransform
    → UIA infrastructure (dedicated MTA thread + timeout/cancellation)
    → UIA inspect/find + pruning
    → WinEvents + UIA events
    → element registry + stale/re-resolution
    → UIA semantic actions
    → foreground/integrity/secure-desktop guards
    → SendInput
    → basic capture
    → observe/act/verify
    → wait primitives
    → CLI
    → deterministic TestApp + automated tests
M2: continuous capture → frame-diff/stability heuristics
M3: WinRT OCR → vision adapter → grounding → scene merge
M4: CDP → model adapters → agent loop → JSON-RPC → MCP wrapper
```

No AI/LLM integration in M1: prove the Windows control runtime is correct
and deterministic on its own first.

---

## Human emergency stop (M5)

"Human always wins." `EmergencyControl` (`src/Inbrisk.Mcp/EmergencyControl.cs`)
is a process-wide singleton holding `controlState: Active | Paused |
EmergencyStopped`. `GlobalHotkeyService`
(`src/Inbrisk.Platform.Windows/Input/GlobalHotkeyService.cs`) registers two
`RegisterHotKey` chords on a dedicated message-loop thread:

- **Panic** `Ctrl+Alt+Pause` (env `INBRISK_PANIC_HOTKEY`) — flips state,
  cancels the epoch token (linked into every MCP action/wait/verification
  token), calls `ReleaseAll()` on every registered `IInputService`, writes a
  persistent stop marker (`%LOCALAPPDATA%/inbrisk/emergency-stop.flag`,
  override `INBRISK_EMERGENCY_STATE`) and a JSONL telemetry event.
- **Resume** `Ctrl+Alt+Shift+Pause` (env `INBRISK_RESUME_HOTKEY`) — local
  human-only; clears the marker and mints a fresh epoch. No MCP tool can
  resume; the resume chord itself is rejected if replayed through
  `computer_key`/`computer_hotkey`.

The stop state survives MCP disconnect and process restart via the marker
file — a new session can never bypass a panic stop. If `RegisterHotKey`
fails (chord taken), the server stays up but control stays
`EmergencyStopped`; the failure is written to stderr + telemetry and
advertised as `emergencyHotkeyAvailable: false` in `computer_observe` and
`inbrisk://capabilities`.

Foundation for future physical-input intervention (`HumanInterventionMode`
enum: `Off | PauseOnKeyboard | PauseOnMouse | PauseOnAnyInput`) is in place;
low-level injected-vs-physical distinction will use `LLKHF_INJECTED`/
`LLMHF_INJECTED` hook flags. The panic hotkey stays independent of and
prioritized over that system.

---

## 16. The Ten Leaps: Autonomous Computer-Use Runtime Architecture

Inbrisk has evolved from an atomic screenshot-clicking API into a unified, autonomous Windows-native control runtime. Rather than requiring models to evaluate the desktop on every tiny sub-action, Inbrisk executes localized behavioral programs, caches spatial and semantic relationships, arbitrates multi-agent desktop leases, and supports seamless human-in-the-loop takeover.

### The Ten Pillars of the Modern Architecture

#### 1. Behavioral Mini-Programs (`scan` / `for_each`)
- Rather than dispatching 50 individual round-trips to process a list, models submit a single structured execution plan with local iteration.
- `computer_run` supports `scan` and `for_each` over containers with local filtering (`where: { role, nameContains, prop }`), property projection (`collect: ["id", "name", "value"]`), and sub-step execution.
- If an ambiguous target or unexpected modal appears, execution pauses locally and reports diagnostic state to the model.

#### 2. Relational Topology & Structured Representation
- Preserves layout semantics instead of flat element lists.
- Relational selectors allow precise targeting without ambiguous name collisions:
  - `within`: Restricts native search to the container's subtree.
  - `ancestor`: Enforces hierarchical containment.
  - `labelledBy`: Connects input fields to their adjacent descriptive labels.
  - `nearText`: Proximity-based disambiguation for unlabeled controls.

#### 3. Persistent Desktop State & Navigation Memory (`computer_screen_memory`)
- `ScreenMemoryStore` maintains a durable model of visited screens, known controls, and navigation transitions across multi-step tasks.
- Tracks element validity states: `Verified` (proven live), `Stale` (needs re-check), and `Predicted` (remembered from prior visits).
- Prevents re-discovering the entire desktop from scratch on every turn.

#### 4. Reusable Semantic Action Recipes (`computer_save_recipe` / `computer_run_recipe`)
- Proven multi-step workflows are saved as parameterized recipes (e.g. `save_file(path)`).
- Replays use semantic UIA and specialist backends rather than coordinate playback.
- Automatically extracts candidates from successful execution runs.

#### 5. Task-Oriented Wait & Condition Filtering (`computer_wait_for`)
- Replaces arbitrary `sleep(2000)` calls with targeted condition predicates.
- Waits for elements to appear, disappear (`gone: true`), or attain expected states (`expectedState: "selected"`, `expectedValue: "ready"`).
- Automatically aborts if an unexpected error dialog or modal window intercepts the active surface.

#### 6. Task-Oriented Observation
- Tailors observation density to the active task: `focusOnly`, `actionsOnly`, or container-scoped.
- Enforces collection pagination and provides explicit completeness indicators (`isComplete: false`, `truncatedCount`).

#### 7. Actionable Failure Diagnosis Layer (`TargetDiagnosis`)
- Eliminates mystery failures. When a target is not found or fails to resolve, Inbrisk categorizes the root cause:
  - `TargetOffscreen` / `TargetOnDifferentScreen`
  - `BudgetExhausted`
  - `VirtualizedOffscreen` (recommends `computer_scroll`)
  - `ElementDisabled`
  - `RelationFilterError`
  - `CloseMatchesFound` (provides actionable `did you mean '...'?` hints)

#### 8. Unified Specialist Application Adapters (`IApplicationAdapter`)
- Specialized applications provide direct semantic control without coordinate clicks.
- `MediaApplicationAdapter` controls Windows system media transport controls (play, pause, next, volume) instantly.
- Seamlessly accessible via `computer_execute_adapter` and `computer_list_adapters`.

#### 9. Multi-Client Desktop Concurrency & Arbitration (`DesktopArbiter`)
- Arbitrates concurrent physical and read-only access when multiple AI sessions or agents share the same Windows session.
- Enforces an exclusive physical input lease (`BeginExclusiveLease`) while permitting concurrent read-only observations.
- Supports targeted plan cancellation (`computer_cancel_task`) without terminating the entire desktop runtime.

#### 10. Human Takeover & Seamless Resume (`computer_pause_run` / `computer_resume_run`)
- Clean safe-point pauses via `computer_pause_run` or plan-level `action: "human"`.
- Floating pill HUD transforms into an informative amber surface:
  - Line 1: `⏸ İnsan kontrolü: [reason]`
  - Line 2: `Sıradaki: [nextStep]`
- Supports "I completed this step" (`skipPausedStep: true`).
- Clears stale element handles (`InvalidateStaleHandles`) and re-observes desktop modifications (`reobserve: true`) to detect window switches or selections before continuing remaining steps.

---

## 17. Runtime & MCP Tool Surface

| Tool Category | Key MCP Tools | Primary Capability |
|---|---|---|
| **Execution** | `computer_run`, `computer_cancel_task` | Local multi-step execution, loops, variables, and task cancellation |
| **Human-in-the-Loop** | `computer_pause_run`, `computer_resume_run` | Safe-point pause, amber HUD status, handle invalidation, seamless resume |
| **Observation** | `computer_observe`, `computer_find`, `computer_inspect` | Semantic UIA inspect, relational search, screenshot capture |
| **Interaction** | `computer_click`, `computer_invoke`, `computer_set_value`, `computer_type`, `computer_key`, `computer_hotkey`, `computer_scroll`, `computer_drag` | Hybrid semantic invoke + native SendInput fallback |
| **Synchronization** | `computer_wait`, `computer_wait_for`, `computer_wait_for_change`, `computer_wait_for_stable` | Predicate-based waiting, dialog detection, pixel & semantic stability |
| **Memory & Recipes** | `computer_screen_memory`, `computer_save_recipe`, `computer_run_recipe`, `computer_list_recipes` | Persistent desktop knowledge, macro parametrization, and replay |
| **Specialist Adapters** | `computer_execute_adapter`, `computer_list_adapters` | Direct media and specialized app integrations |
| **Safety & Control** | `computer_reset_input`, `computer_app_status`, `computer_app_restart` | Input recovery, overlay click-through fix, emergency telemetry |

