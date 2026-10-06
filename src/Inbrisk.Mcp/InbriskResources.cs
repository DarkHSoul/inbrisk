using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Inbrisk.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

/// <summary>
/// One read-only resource: server capabilities. Windows/active-window state
/// is intentionally NOT exposed as resources — computer_windows and
/// computer_observe already cover it with richer semantics; duplicating it
/// as URIs would only add noise.
/// </summary>
[McpServerResourceType]
public static class InbriskResources
{
    [McpServerResource(UriTemplate = "inbrisk://capabilities",
        Name = "inbrisk-capabilities", MimeType = "application/json"),
     Description("Inbrisk runtime capabilities: tool contracts, computer_run " +
        "step schema, target spec, error model, coordinate semantics.")]
    public static string Capabilities() => CapabilitiesJson();

    /// <summary>The full machine-readable reference — shared by the
    /// inbrisk://capabilities resource and the computer_capabilities tool.</summary>
    internal static string CapabilitiesJson(
        ComputerControlActivityService? activity = null) => JsonSerializer.Serialize(new
    {
        server = "inbrisk",
        build = BuildProvenance(),
        controlState = EmergencyControl.Process.State.ToString(),
        indicator = activity == null ? null : new
        {
            state = activity.State.ToString(),
            activeClients = activity.ActiveClients,
            activeLeases = activity.ActiveLeases,
        },
        emergencyHotkey = EmergencyControl.Process.PanicHotkey,
        emergencyHotkeyAvailable = EmergencyControl.Process.PanicAvailable,
        panicAuthority = EmergencyControl.Process.PanicAvailable ? "local"
            : EmergencyControl.Process.PanicDelegated ? "peer-process" : "none",
        resumeHotkey = EmergencyControl.Process.ResumeHotkey,
        resumeHotkeyAvailable = EmergencyControl.Process.ResumeAvailable,
        observationModes = new[] { "auto", "semantic", "visual", "both" },
        coordinateSpace = "image-space (per-frame; always pass frameId + observationId)",
        confirmation = "the client's explicit tool call is the confirmation; " +
            "Deny-classified targets (elevated, password, kill-switch) still refuse",
        lifecyclePolicy = new
        {
            rule1 = "Never close, minimize, or alter applications that were already open before the task started (ownership: user).",
            rule2 = "Applications opened by the agent (ownership: agent) may be closed when no longer required and there is no unsaved user data.",
            rule3 = "Prefer graceful close (computer_close_window). Never force-kill.",
            rule4 = "Do not close the agent host, terminal, IDE, Colab browser, or protected apps.",
            rule5 = "After closing an application, notify the user: 'Closed <app> — no longer needed for this task.'",
            rule6 = "When in doubt, leave it open. Cost of leaving open is minimal; cost of closing user apps causes data loss.",
        },

        // ---------------- semantic target spec ----------------
        targetSpec = new
        {
            fields = new[]
            {
                "elementId — id from find/observe (always wins)",
                "window — top-level title substring (LOCALIZED) or hwnd",
                "process — exe name substring, e.g. \"notepad\" (locale-safe, prefer this)",
                "role — button|edit|document|combobox|menuitem|treeitem|tabitem|list|text|...",
                "name — case-insensitive substring of the element name",
                "automationId — exact AutomationId",
            },
            properties = new[]
            {
                "all case-insensitive, AND'd, filtered server-side",
                "nameContains — substring (same as name)",
                "nameNotContains — reject when name contains this",
                "value — exact value-property match",
                "valueContains — value substring, e.g. \"/artist/\" in a hyperlink URL",
                "valueNotContains — reject when value contains this",
                "className — exact className-property match",
            },
            relationships = new[]
            {
                "labelledBy — element's label text (UIA LabeledBy, else a " +
                    "nearby Text element left-of/above the target)",
                "nearText — a Text element containing this within ~200px",
                "within — elementId, hwnd, or \"$ref\" (e.g. a launch-bound " +
                    "window) the target must be inside",
                "ancestor — role or name an ancestor in the UIA path must match",
            },
            resolution = "candidates are scored for the requested action " +
                "(editable controls win for set_value/type; invokable for " +
                "invoke/click). A static label never beats a real control " +
                "unless you ask for role:\"Text\" explicitly. Equal scores " +
                "→ AmbiguousTarget with the candidate list — refine and retry, " +
                "or disambiguate deterministically with select/orderBy on " +
                "the step (server-side pick, never a blind first).",
        },

        // ---------------- computer_run step schema ----------------
        runSteps = new
        {
            universal = "every step may carry: action, retry, retryInterval, " +
                "timeout, ifExists, ifExistsId, ifNotExists, ifNotExistsId, " +
                "ifEnabled, ifEnabledId, ifValue — plus a target spec where " +
                "meaningful. retry only re-evaluates TRANSIENT states " +
                "(TargetNotFound, Stale, assert mismatch) — " +
                "Malformed/AmbiguousTarget/PolicyDenied/EmergencyStopped never retry. " +
                "selection: select:\"first|last|nth\" + index + orderBy:" +
                "\"visual|tree|score\" picks server-side among equal candidates",
            actions = new Dictionary<string, object>
            {
                ["launch"] = new { fields = "app|search|executable|path|" +
                    "aumid|uri, arguments[], newInstance, waitFor, debugPort, as, " +
                    "timeout|ms", note =
                    "same pipeline as computer_launch; debugPort enables Chrome DevTools Protocol; as binds the app's " +
                    "top-level hwnd for within:/wait_for scoping" },
                ["adapter"] = new { fields = "action, adapter, target, args", note =
                    "direct specialist adapter execution: chrome_devtools (navigate, click, type, evaluate, get_content, list_tabs, new_tab, close_tab) | media (play, pause, next, volume) | testapp" },
                ["find"] = new { fields = "target|elementId, as, select, index, orderBy", note = "binds result to $as; reports matchCount" },
                ["assert"] = new { fields = "elementId|target + contains|notContains|exact|state|enabled|value (+ select/index/orderBy)", note = "retry covers async UI flips" },
                ["checkpoint"] = new { fields = "note", note = "pauses run, returns delta — resume via same runId" },
                ["focus"] = new { fields = "elementId|target|hwnd" },
                ["focus_window"] = new { fields = "hwnd|target(window/process)" },
                ["click|rightclick|doubleclick|invoke|toggle|select|hover"] =
                    new { fields = "elementId|target, or x+y+frameId+observationId" },
                ["set_value"] = new { fields = "elementId|target + text|value" },
                ["type"] = new { fields = "elementId|target + text, mode:replace|append|insert, position:current|start|end, submit" },
                ["key"] = new { fields = "key, count" },
                ["hotkey"] = new { fields = "key+modifiers:[ctrl,shift,alt,win]  OR  keys:\"ctrl+s\"" },
                ["scroll"] = new { fields = "delta (+ elementId|target|point)" },
                ["drag"] = new { fields = "elementId|target|point + toX+toY (+toFrameId+toObservationId)" },
                ["wait"] = new { fields = "ms" },
                ["wait_for"] = new { fields = "query, ms, target", note =
                    "target scopes the watch: {within:\"$ref\"|hwnd} → that " +
                    "window's subtree, {process} → all its windows, " +
                    "{window} → title match. Works on hwnd-less UIA trees " +
                    "(Spotify/Electron); no target → observed window, then desktop" },
                ["wait_for_change|wait_for_stable"] = new { fields = "ms" },
                ["wait_for_gone"] = new { fields = "query|target|elementId, ms, stopOnDialog" },
                ["scroll_into_view"] = new { fields = "elementId|target" },
                ["scan|for_each"] = new { fields = "target|elementId " +
                    "(container), as, where{startsWith,contains,notContains," +
                    "exact,role,state}, steps[], collect[], maxItems, " +
                    "maxPages, stopOn[]", note =
                    "iterates matched items SERVER-SIDE — no LLM roundtrip " +
                    "per item; binds $as (default $item) plus .id/.name/" +
                    ".role/.value for sub-steps; collect projects props into " +
                    "the run's collected[]; maxPages paginates virtualized lists" },
                ["human|human_takeover|pause_for_human"] = new
                    { fields = "note|reason", note =
                    "pauses the run and hands control to the local user — " +
                    "resume via computer_resume_run" },
                ["media|play|pause|next|previous|volume_up|volume_down|mute"] =
                    new { fields = "target(process)|adapter|args", note =
                    "routed through specialist adapters like action:\"adapter\"" },
            },
            recipes = "proven runs → reusable parameterized macros: " +
                "computer_save_recipe{name, fromRunId|steps with " +
                "{{param}} placeholders, parameters[]} then " +
                "computer_run_recipe{name, parameters:{…}}; " +
                "computer_list_recipes reports success rates",
            bindings = "find{as:\"doc\"} → later steps use elementId:\"$doc\", " +
                "target:{elementId:\"$doc\"} or target:{within:\"$doc\"}; " +
                "launch{as:\"app\"} binds a window hwnd the same way",
            strictValidation = "unknown fields are rejected (Malformed) — " +
                "a field valid for another action is NOT silently ignored",
            pauseSemantics = "first failure pauses the run with step, error, " +
                "observationDelta and availableElements; resume with the same " +
                "runId and new steps. An unexpected dialog pauses with " +
                "UnexpectedModalOpened.",
        },

        // ---------------- application launch ----------------
        launch = new
        {
            tool = "computer_launch — resolve + start a Windows app WITHOUT " +
                "shell/PowerShell. Prefer it whenever the user asks to " +
                "interact with an app that may not be running.",
            catalog = "computer_apps{name:\"query\"} searches the " +
                "launchable-app catalog and returns only matches, each " +
                "with its exact launch argument — always pass a name; " +
                "the full dump needs all:true. kind:\"installed\"|\"system\" " +
                "filters user apps vs Windows inbox components. " +
                "computer_launch{search:\"name\"} searches the catalog " +
                "and opens the best match in one call",
            identifiers = new[]
            {
                "app — friendly name (\"Spotify\", \"Notepad\", \"Calculator\") — preferred",
                "executable — exe name/path (\"notepad.exe\")",
                "path — explicit path; non-exe files (e.g. .uproject) open " +
                    "via the registered file association",
                "aumid — packaged-app id (PackageFamilyName!AppId)",
                "uri — registered protocol (\"spotify:\")",
                "debugPort — optional remote debugging port for Chrome/Chromium DevTools Protocol (CDP, e.g. 9222)",
            },
            resolution = "deterministic pipeline: existing top-level window " +
                "→ Start Menu shortcuts → App Paths → packaged/Store apps " +
                "(AUMID) → executable on PATH/alias dirs → filesystem exe " +
                "scan under install roots (finds engines/dev tools that " +
                "register nowhere, e.g. UnrealEditor.exe) → registered URI " +
                "scheme. Non-exe path: resolves via the file's registered " +
                "association. method reports which mechanism produced the " +
                "launch: ExistingInstance|StartMenu|AppPath|Aumid|" +
                "Executable|ExplicitPath|Protocol.",
            readiness = "waitFor: window (default — usable, UIA-reachable " +
                "top-level window; verifies far more than process spawn) | " +
                "process | none; timeoutMs default 10000",
            idempotent = "a running app is reused (launchState:" +
                "AlreadyRunning) unless newInstance:true — the bound window " +
                "is the user-facing one, never a helper process",
            security = "mutating computer action — denied while " +
                "emergency-stopped, never a shell: arguments are a " +
                "structured array and interpreters (cmd/powershell/mshta/" +
                "rundll32/…) are refused with PolicyDenied",
            ambiguity = "a friendly name matching several DISTINCT apps " +
                "returns AmbiguousApplication + candidates — pick one and " +
                "call computer_launch again immediately with its " +
                "identifier; do not narrate intermediate steps",
            runBinding = "computer_run step action:\"launch\" takes the same " +
                "fields plus as:\"name\" → the app's top-level hwnd binds to " +
                "$name for within:/wait_for scoping",
        },

        verification = new[]
        {
            "Verified — evidence attached (ValueReadback/PropertyChanged/ForegroundWindow/ElementGone/SemanticEvent)",
            "Unverified — executed but no checkable postcondition",
            "Failed — postcondition checked and did not hold",
        },
        errors = new[]
        {
            "TargetNotFound", "AmbiguousTarget", "AmbiguousApplication",
            "Stale", "StaleFrame",
            "WindowLost", "WindowGeometryChanged", "TargetElevated",
            "SecureDesktop", "PolicyDenied", "ConfirmationRequired",
            "ConfirmationDenied", "Timeout", "Cancelled",
            "CaptureUnavailable", "InputRejected", "Malformed",
            "Unverified", "NoProgress", "EmergencyStopped",
        },
        hostEnvironment = new
        {
            note = "AI Environment Awareness: Identify and preserve your hosting infrastructure.",
            rules = "Never close, hide, or terminate the hosting terminal (Windows Terminal, cmd, powershell, Qwen, Claude), parent IDE (VS Code, Antigravity, Cursor), or browser tabs running Google Colab / Jupyter notebooks powering your backend. computer_close_window protects these windows and will reject closures.",
            modalsAndDialogs = "Windows applications and the OS open modal dialogs and modern system flyouts (error messages, confirmation prompts, Share/Paylaş flyouts, setup wizards) that block background windows. Inbrisk surfaces [MODAL-OR-POPUP-ACTIVE], [MODAL/DIALOG], and [BLOCKED by popup/dialog] in inspect, windows, and observe tools. Dismiss or interact with the dialog/flyout first (e.g. computer_hotkey with key:'Escape' or click its controls)."
        },
        usage = new[]
        {
            "to interact with an app that may not be running, use " +
                "computer_launch (or a computer_run launch step) — never " +
                "shell/PowerShell to locate or start it",
            "act directly on a known semantic target — find/observe are for discovery",
            "prefer elementIds over coordinates",
            "prefer process over window (titles are localized)",
            "coordinates: image-space only, always with frameId + observationId",
            "check status + evidence in every action result",
            "re-observe after Stale/StaleFrame",
            "verified actions return post-state + changes — do not re-observe to confirm",
            "desktop input stuck (dead clicks, phantom modifier) → computer_reset_input " +
                "releases all keys/buttons and can fix input-swallowing windows",
            "environment awareness: NEVER close or kill your host terminal, IDE, or Colab/Jupyter notebook tabs — only close user application windows that you opened for the task",
            "system flyouts & modal popups: modern flyouts (e.g. Share/Paylaş, Open with) and dialogs (e.g. Save As, error popups) block desktop and background window input. When an active popup is reported, you must dismiss it (press Escape via computer_hotkey(key: 'Escape') or click its controls) before background interactions will succeed.",
        },
    });

    /// <summary>Compact cheat-sheet for small-context models — same facts
    /// as the full schema minus per-action field tables. ~6x smaller;
    /// computer_capabilities{detail:"full"} returns the complete schema.</summary>
    internal static string CapabilitiesJsonSlim(
        ComputerControlActivityService? activity = null) => JsonSerializer.Serialize(new
    {
        server = "inbrisk",
        version = typeof(InbriskResources).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(InbriskResources).Assembly.GetName().Version?.ToString(),
        controlState = EmergencyControl.Process.State.ToString(),
        emergencyHotkey = EmergencyControl.Process.PanicHotkey,
        resumeHotkey = EmergencyControl.Process.ResumeHotkey,
        outputDetail =
            "find|observe|inspect|run|capabilities accept " +
            "detail:\"slim\"|\"full\"; default via INBRISK_DETAIL env or " +
            "settings.json outputDetail",
        tools =
            "computer_apps(name:required for search, kind, detail) — search launchable apps | " +
            "browser_browse(url, newTab?, port?, tabId?) — one-call Chrome " +
            "navigation (self-heals: auto-spawns a debuggable browser if " +
            "none is up; prefer it over launch+adapter for 'go to page') — " +
            "siblings: browser_click, browser_type, browser_evaluate, " +
            "browser_content, browser_tabs, browser_screenshot | " +
            "computer_launch(app|search|executable|path|aumid|uri|debugPort) | " +
            "computer_adapter(action, adapter:chrome_devtools|media|testapp, target, args) | " +
            "computer_list_adapters | " +
            "computer_find(target fields, limit, detail) | " +
            "computer_observe(mode, hwnd, detail, maxElements) | " +
            "computer_windows | computer_inspect(hwnd|elementId, detail) | " +
            "computer_run(steps, runId, detail) | computer_reset_input | " +
            "computer_ui_status|ui_set | computer_app_status|app_restart",
        targetSpec =
            "elementId|window|process|role|name|automationId + " +
            "nameNotContains|value|valueContains|valueNotContains|className + " +
            "labelledBy|nearText|within|ancestor — all case-insensitive AND'd; " +
            "ambiguous matches → AmbiguousTarget, refine or use " +
            "select/orderBy on the step",
        runStepActions =
            "launch|find|assert|checkpoint|focus|focus_window|click|rightclick|" +
            "doubleclick|invoke|toggle|select|hover|set_value|type|key|hotkey|" +
            "scroll|drag|wait|wait_for|wait_for_gone|wait_for_change|" +
            "wait_for_stable|scan|for_each|adapter|media|human|pause_for_human",
        specialistAdapters = "chrome_devtools (CDP DOM/JS/tabs) | media (Spotify/VLC) | testapp",
        runBindings =
            "find{as:\"x\"}/launch{as:\"x\"} → later steps use " +
            "elementId:\"$x\" or target:{within:\"$x\"}",
        coordinateSpace = "image-space only — always pass frameId + observationId",
        errors = new[]
        {
            "TargetNotFound", "AmbiguousTarget", "Stale", "StaleFrame",
            "WindowLost", "PolicyDenied", "ConfirmationRequired", "Timeout",
            "Cancelled", "InputRejected", "Malformed", "Unverified",
            "NoProgress", "EmergencyStopped", "AmbiguousApplication",
            "TargetElevated", "SecureDesktop", "CaptureUnavailable",
            "ConfirmationDenied", "WindowGeometryChanged",
        },
        usage = new[]
        {
            "prefer elementIds over coordinates; prefer process over window title",
            "verified actions return post-state — do not re-observe to confirm",
            "re-observe after Stale/StaleFrame",
            "full schema: computer_capabilities{detail:\"full\"} " +
                "or resource inbrisk://capabilities",
        },
    });

    /// <summary>Which binary answered this handshake — version, build time,
    /// commit (when built inside a repo) and the executable's own file
    /// stamp/hash so a stale-binary mistake is detectable, not invisible.</summary>
    private static object BuildProvenance()
    {
        var asm = typeof(InbriskResources).Assembly;
        string? Meta(string key) => asm.GetCustomAttributes<
            AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
        string? exeStamp = null, exeSha = null, codeSha = null;
        var exe = Environment.ProcessPath;
        try
        {
            if (exe != null)
            {
                exeStamp = File.GetLastWriteTimeUtc(exe).ToString("o");
                using var sha = System.Security.Cryptography.SHA256.Create();
                exeSha = Convert.ToHexString(
                    sha.ComputeHash(File.ReadAllBytes(exe)))[..16];
            }
            // The exe is an apphost stub — byte-identical across rebuilds.
            // Hash the managed Inbrisk assemblies instead; that digest only
            // stays constant when the shipped code actually is identical.
            var dir = Path.GetDirectoryName(exe ?? asm.Location);
            if (dir != null)
            {
                // dev build: hash the managed assemblies. single-file
                // release: there are none — the exe itself IS the code.
                var files = Directory.EnumerateFiles(dir, "Inbrisk*.dll")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0 && exe != null) files.Add(exe);
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var ms = new MemoryStream();
                foreach (var f in files)
                {
                    ms.Write(System.Text.Encoding.UTF8.GetBytes(
                        Path.GetFileName(f) + "\0"));
                    ms.Write(File.ReadAllBytes(f));
                }
                codeSha = Convert.ToHexString(
                    sha.ComputeHash(ms.ToArray()))[..16];
            }
        }
        catch { }
        return new
        {
            version = asm.GetCustomAttribute<
                AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? asm.GetName().Version?.ToString() ?? "0.0.0",
            commit = string.IsNullOrWhiteSpace(Meta("BuildCommit"))
                ? null : Meta("BuildCommit"),
            timestamp = Meta("BuildTimestamp"),
            exe,
            exeStamp,
            exeSha256_16 = exeSha,
            codeSha256_16 = codeSha,
        };
    }
}
