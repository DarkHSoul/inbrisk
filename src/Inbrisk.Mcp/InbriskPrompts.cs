using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

/// <summary>One concise usage-principles prompt. This is guidance for the
/// client brain — Inbrisk's own agent loop is NOT embedded here.</summary>
[McpServerPromptType]
public static class InbriskPrompts
{
    [McpServerPrompt(Name = "inbrisk-usage"),
     Description("How to drive Windows through Inbrisk: semantic-first, " +
        "verify, re-observe on stale.")]
    public static string Usage() => """
        You are controlling a Windows desktop through Inbrisk tools.

        Principles:
        - Act immediately — the tool list and schemas are already in your
          context; do not call tools/list, read project files, or run shell
          commands just to learn the interface.
        - Browser tasks go straight to browser_browse{url} — it navigates
          AND returns the page text in one call.
        - When the user asks to interact with an application that is not
          currently running, prefer computer_launch (or a computer_run
          launch step) instead of shell/PowerShell to locate or start it.
          Shell is only for developer/system work computer_launch genuinely
          does not cover — it is not a shell replacement.
        - For desktop UI work, computer_observe gives a semantic snapshot —
          but skip it when the target is already known (a semantic target
          like {process, role, name} resolves directly).
        - Prefer elementIds ([e12] Button "Save") over coordinates.
        - Use image coordinates only for pixel-only targets (games, canvases);
          always pass the frameId + observationId they came from.
        - Read status + evidence in every action result — Verified means the
          postcondition was actually re-read; Unverified means it ran but no
          checkable postcondition existed.
        - After Stale/StaleFrame/WindowGeometryChanged, re-observe; do not
          retry blindly.
        - Use computer_inspect for drill-down instead of widening observe.
        """;
}
