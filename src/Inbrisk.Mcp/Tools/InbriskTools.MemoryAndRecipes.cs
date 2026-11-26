using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

public sealed partial class InbriskTools
{
    [McpServerTool(Name = "computer_save_recipe"), Description(
        "Save a proven sequence of RunSteps as a reusable semantic recipe/macro. " +
        "Can save steps from an existing run via fromRunId, or from explicitly provided steps. " +
        "Recipes can have parameterized inputs (e.g. {{songTitle}}, {{targetPlaylist}}) " +
        "and allow future executions to run locally without re-discovering the entire UI.")]
    public Task<CallToolResult> SaveRecipe(
        [Description("unique recipe identifier name (e.g. 'spotify_add_to_playlist')")] string name,
        [Description("runId of a completed or paused run to save steps from — preferred over passing raw steps")] string? fromRunId = null,
        [Description("ordered recipe steps with optional {{parameter}} placeholders (as JSON array)")] JsonElement? steps = null,
        [Description("target application name (e.g. 'Spotify')")] string? app = null,
        [Description("human/model readable explanation of what this recipe accomplishes")] string? description = null,
        [Description("parameter declarations expected by this recipe")] RecipeParameter[]? parameters = null,
        [Description("precondition hints checked before execution")] string[]? preconditions = null,
        [Description("postcondition hints verified upon completion")] string[]? postconditions = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(Error(OutcomeKind.Malformed, "recipe name is required"));
        if (TaskRecipeStore.SanitizeName(name) is null)
            return Task.FromResult(Error(OutcomeKind.Malformed,
                $"recipe name '{name}' has no filename-usable characters — " +
                $"disambiguate it (e.g. 'spotify_add_to_playlist') and retry"));

        // F11: persisting recipes mutates user state — honour the latched
        // emergency stop like every other mutating tool.
        if (_s.Control.ActionToken() == null)
            return Task.FromResult(Error(OutcomeKind.EmergencyStopped, StoppedDetail));

        RunStep[]? resolvedSteps = null;
        if (!string.IsNullOrWhiteSpace(fromRunId))
        {
            if (!_s.Runs.TryGetValue(fromRunId, out var state) || state.PlannedSteps == null || state.PlannedSteps.Length == 0)
                return Task.FromResult(Error(OutcomeKind.TargetNotFound, $"run '{fromRunId}' has no recorded steps to save"));
            resolvedSteps = state.PlannedSteps;
        }
        else if (steps.HasValue && steps.Value.ValueKind != JsonValueKind.Null && steps.Value.ValueKind != JsonValueKind.Undefined)
        {
            try
            {
                resolvedSteps = JsonSerializer.Deserialize<RunStep[]>(steps.Value.GetRawText(), J);
            }
            catch (Exception ex)
            {
                return Task.FromResult(Error(OutcomeKind.Malformed, $"Invalid steps JSON: {ex.Message}"));
            }
        }

        if (resolvedSteps is not { Length: > 0 })
            return Task.FromResult(Error(OutcomeKind.Malformed, "recipe requires at least 1 step (provide fromRunId or steps)"));

        for (var i = 0; i < resolvedSteps.Length; i++)
        {
            if (ValidateStep(resolvedSteps[i]) is { } verr)
                return Task.FromResult(Error(OutcomeKind.Malformed, $"recipe step [{i}]: {verr}"));
        }

        var generalizedSteps = new RunStep[resolvedSteps.Length];
        for (var i = 0; i < resolvedSteps.Length; i++)
            generalizedSteps[i] = GeneralizeRecipeStep(resolvedSteps[i], app);

        var stepsJson = JsonSerializer.Serialize(generalizedSteps, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var def = new TaskRecipeDefinition(
            Name: name,
            Description: description,
            App: app,
            Parameters: parameters ?? Array.Empty<RecipeParameter>(),
            Preconditions: preconditions ?? Array.Empty<string>(),
            StepsJson: stepsJson,
            Postconditions: postconditions ?? Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow);

        _s.RecipeStore.Save(def);
        return Task.FromResult(Text($"✓ Recipe '{name}' saved successfully with {resolvedSteps.Length} steps."));
    }

    private static RunStep GeneralizeRecipeStep(RunStep step, string? defaultApp)
    {
        var target = step.Target;
        if (target != null)
        {
            var targetElId = target.ElementId;
            if (targetElId != null && targetElId.StartsWith("uia_", StringComparison.OrdinalIgnoreCase))
                targetElId = null;

            var process = target.Process ?? defaultApp;
            target = target with
            {
                Hwnd = null,
                Process = process,
                ElementId = targetElId
            };
        }

        var elId = step.ElementId;
        if (elId != null && elId.StartsWith("uia_", StringComparison.OrdinalIgnoreCase))
            elId = null;

        RunStep[]? subSteps = null;
        if (step.Steps != null)
        {
            subSteps = new RunStep[step.Steps.Length];
            for (var j = 0; j < step.Steps.Length; j++)
                subSteps[j] = GeneralizeRecipeStep(step.Steps[j], defaultApp);
        }

        return step with
        {
            Target = target,
            ElementId = elId,
            Hwnd = null,
            ObservationId = null,
            FrameId = null,
            ToObservationId = null,
            ToFrameId = null,
            Steps = subSteps
        };
    }

    /// <summary>save_as_recipe post-success hook shared by computer_run,
    /// computer_do and computer_batch: when the plan result reports success
    /// (status Completed / success:true) the executed steps are generalized
    /// through the same path computer_save_recipe uses and persisted under
    /// task-recipes/&lt;name&gt;.json. A failed or partial run never saves —
    /// the result passes through untouched. Name/save problems attach a
    /// recipeWarning field instead of failing the action.</summary>
    private CallToolResult AttachSavedRecipe(CallToolResult res,
        string? saveAsRecipe, IReadOnlyList<RunStep> executedSteps,
        string? app, string toolName)
    {
        if (string.IsNullOrWhiteSpace(saveAsRecipe) ||
            res.Content is not [TextContentBlock { Text: { } rawText }])
            return res;

        Dictionary<string, object?>? doc;
        try { doc = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawText, J); }
        catch { return res; }
        if (doc == null) return res;

        var completed =
            (doc.TryGetValue("status", out var st) &&
             st is JsonElement { ValueKind: JsonValueKind.String } se &&
             string.Equals(se.GetString(), "Completed",
                 StringComparison.OrdinalIgnoreCase)) ||
            (doc.TryGetValue("success", out var sv) &&
             sv is JsonElement { ValueKind: JsonValueKind.True });
        if (!completed) return res;

        var (recipe, warning) =
            SaveStepsAsRecipe(saveAsRecipe.Trim(), executedSteps, app, toolName);
        if (recipe == null && warning == null) return res;
        if (recipe != null) doc["recipe"] = recipe;
        if (warning != null) doc["recipeWarning"] = warning;

        return new CallToolResult
        {
            IsError = res.IsError,
            Content = [new TextContentBlock
                { Text = JsonSerializer.Serialize(doc, J) }],
        };
    }

    /// <summary>Persist executed steps as a reusable recipe — the same
    /// generalize + store path as computer_save_recipe. When a
    /// RecipeParameterizer component is present it first rewrites
    /// dynamic-looking values (urls, file paths, typed free text) into
    /// {{param}} placeholders; otherwise the recipe is saved verbatim with
    /// parameterized:false. Returns the recipe block for the tool result,
    /// or a warning — never throws.</summary>
    private (Dictionary<string, object?>? Recipe, string? Warning)
        SaveStepsAsRecipe(string rawName, IReadOnlyList<RunStep> steps,
            string? app, string toolName)
    {
        if (TaskRecipeStore.SanitizeName(rawName) == null)
            return (null, $"save_as_recipe '{rawName}' has no usable " +
                "filename characters — recipe not saved");
        if (steps.Count == 0)
            return (null, "save_as_recipe skipped — no executed steps to save");

        var parameterized = TryParameterizeSteps(steps,
            out var parameterizedSteps, out var detectedParams);
        var source = parameterized ? parameterizedSteps : steps;

        var generalized = new RunStep[source.Count];
        for (var i = 0; i < source.Count; i++)
            generalized[i] = GeneralizeRecipeStep(source[i], app);

        var stepsJson = JsonSerializer.Serialize(generalized,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization
                    .JsonIgnoreCondition.WhenWritingNull
            });

        var replaced = _s.RecipeStore.Get(rawName) != null;
        var def = new TaskRecipeDefinition(
            Name: rawName,
            Description: $"auto-saved from a successful {toolName} call",
            App: app,
            Parameters: detectedParams ?? Array.Empty<RecipeParameter>(),
            Preconditions: Array.Empty<string>(),
            StepsJson: stepsJson,
            Postconditions: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow);
        try { _s.RecipeStore.Save(def); }
        catch (Exception ex)
        {
            return (null,
                $"save_as_recipe '{rawName}' failed to persist: {ex.Message}");
        }

        var recipe = new Dictionary<string, object?>
        {
            ["name"] = rawName,
            ["path"] = _s.RecipeStore.PathFor(rawName),
            ["steps"] = generalized.Length,
            ["parameterized"] = parameterized,
        };
        if (detectedParams is { Length: > 0 })
            recipe["params"] = detectedParams.Select(p => p.Name).ToArray();
        if (replaced) recipe["replaced"] = true;
        return (recipe, null);
    }

    /// <summary>Optional RecipeParameterizer bridge — the parameterizer is
    /// delivered as a separate component exposing Extract(steps) →
    /// (steps', params). It is invoked via reflection so this compiles and
    /// falls back cleanly whether or not it has merged: absent or
    /// incompatible shape → false, and callers save verbatim with
    /// parameterized:false.</summary>
    private static bool TryParameterizeSteps(IReadOnlyList<RunStep> steps,
        out RunStep[] parameterizedSteps, out RecipeParameter[]? parameters)
    {
        parameterizedSteps = [];
        parameters = null;
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Inbrisk.Mcp.RecipeParameterizer")
                    ?? a.GetType("Inbrisk.Runtime.RecipeParameterizer")
                    ?? a.GetType("Inbrisk.Core.RecipeParameterizer"))
                .FirstOrDefault(t => t != null);
            var extract = type?.GetMethods(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Extract" &&
                    m.GetParameters().Length >= 1);
            var argType = extract?.GetParameters()[0].ParameterType;
            if (extract == null || argType == null) return false;

            object? arg = steps.ToArray();
            if (!argType.IsAssignableFrom(arg.GetType()))
            {
                if (argType.IsAssignableFrom(typeof(List<RunStep>)))
                    arg = steps.ToList();
                else
                    return false;
            }
            var ret = extract.Invoke(null, [arg]);
            if (ret == null) return false;

            // (steps', params) value tuple, or an object with
            // Steps/Parameters members
            object? stepsObj = null, paramsObj = null;
            var rt = ret.GetType();
            if (rt.FullName?.StartsWith("System.ValueTuple",
                    StringComparison.Ordinal) == true)
            {
                stepsObj = rt.GetField("Item1")?.GetValue(ret);
                paramsObj = rt.GetField("Item2")?.GetValue(ret);
            }
            else
            {
                stepsObj = rt.GetProperty("Steps")?.GetValue(ret);
                paramsObj = rt.GetProperty("Parameters")?.GetValue(ret)
                    ?? rt.GetProperty("Params")?.GetValue(ret);
            }
            if (stepsObj is not System.Collections.IEnumerable stepsEnum)
                return false;
            var arr = stepsEnum.Cast<RunStep>().ToArray();
            if (arr.Length == 0) return false;
            parameterizedSteps = arr;

            if (paramsObj is IEnumerable<RecipeParameter> rp)
            {
                parameters = rp.ToArray();
            }
            else if (paramsObj is System.Collections.IEnumerable pEnum)
            {
                // a foreign parameter type — keep whatever exposes a Name
                parameters = pEnum.Cast<object?>()
                    .Select(p => p?.GetType().GetProperty("Name")
                        ?.GetValue(p) as string)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => new RecipeParameter(n!))
                    .ToArray();
            }
            return true;
        }
        catch { return false; }
    }

    private static void SubstituteInJsonNode(System.Text.Json.Nodes.JsonNode node, Dictionary<string, string> parameters)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            var keys = obj.Select(kv => kv.Key).ToList();
            foreach (var key in keys)
            {
                var val = obj[key];
                if (val is System.Text.Json.Nodes.JsonValue jVal && jVal.TryGetValue<string>(out var str))
                {
                    foreach (var (k, v) in parameters)
                    {
                        if (str.Contains("{{" + k + "}}", StringComparison.OrdinalIgnoreCase))
                        {
                            str = System.Text.RegularExpressions.Regex.Replace(
                                str, @"\{\{" + System.Text.RegularExpressions.Regex.Escape(k) + @"\}\}", v ?? "",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        }
                    }
                    obj[key] = str;
                }
                else if (val != null)
                {
                    SubstituteInJsonNode(val, parameters);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var val = arr[i];
                if (val is System.Text.Json.Nodes.JsonValue jVal && jVal.TryGetValue<string>(out var str))
                {
                    foreach (var (k, v) in parameters)
                    {
                        if (str.Contains("{{" + k + "}}", StringComparison.OrdinalIgnoreCase))
                        {
                            str = System.Text.RegularExpressions.Regex.Replace(
                                str, @"\{\{" + System.Text.RegularExpressions.Regex.Escape(k) + @"\}\}", v ?? "",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        }
                    }
                    arr[i] = str;
                }
                else if (val != null)
                {
                    SubstituteInJsonNode(val, parameters);
                }
            }
        }
    }

    [McpServerTool(Name = "computer_run_recipe"), Description(
        "Execute a previously saved semantic task recipe. Replaces {{parameter}} " +
        "placeholders with provided parameter values and runs the plan locally. " +
        "If a step fails or UI changes, pauses with diagnostic context for recovery.")]
    public async Task<CallToolResult> RunRecipe(
        [Description("name of the saved recipe to execute")] string name,
        [Description("key-value parameters for placeholder substitution {{param}}")] Dictionary<string, string>? parameters = null,
        [Description("optional resume runId")] string? runId = null,
        [Description("output verbosity: slim|full")] string? detail = null,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error(OutcomeKind.Malformed, "recipe name is required");

        var normArgs = $"{name}|{runId}|{detail}|" + JsonSerializer.Serialize(parameters ?? new Dictionary<string, string>(), J);

        return await ExecuteWithIdempotencyAsync(operationId, "computer_run_recipe", normArgs, async () =>
        {
            var recipe = _s.RecipeStore.Get(name);
            if (recipe == null)
            {
                var known = _s.RecipeStore.List().Select(r => r.Name);
                return Error(OutcomeKind.TargetNotFound,
                    $"recipe '{name}' not found. Available recipes: [{string.Join(", ", known)}]");
            }

            RunStep[] steps;
            try
            {
                if (parameters != null && parameters.Count > 0)
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(recipe.StepsJson)
                        ?? throw new JsonException("null json node");
                    SubstituteInJsonNode(node, parameters);
                    steps = node.Deserialize<RunStep[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new JsonException("null deserialization");
                }
                else
                {
                    steps = JsonSerializer.Deserialize<RunStep[]>(recipe.StepsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new JsonException("null deserialization");
                }
            }
            catch (Exception ex)
            {
                return Error(OutcomeKind.Malformed, $"failed to deserialize substituted recipe steps: {ex.Message}");
            }

            var sw = Stopwatch.StartNew();
            var res = await RunPlanCore(steps, runId, sw, ct, detail);
            var success = res.IsError != true;
            _s.RecipeStore.RecordRun(name, success);
            return res;
        });
    }

    [McpServerTool(Name = "computer_list_recipes"), Description(
        "List all available semantic task recipes with their parameters, apps, and success history.")]
    public Task<CallToolResult> ListRecipes()
    {
        var recipes = _s.RecipeStore.List();
        if (recipes.Count == 0)
            return Task.FromResult(Text("No task recipes currently saved. Save new workflows via computer_save_recipe."));

        var sb = new StringBuilder();
        sb.AppendLine($"Found {recipes.Count} saved task recipe(s):");
        foreach (var r in recipes)
        {
            var paramStr = r.Parameters.Count > 0
                ? string.Join(", ", r.Parameters.Select(p => p.Name))
                : "none";
            var successRate = (r.SuccessCount + r.FailureCount) > 0
                ? $"{(double)r.SuccessCount / (r.SuccessCount + r.FailureCount) * 100:F0}%"
                : "untested";

            sb.AppendLine($"• {r.Name} [App: {r.App ?? "Generic"}]");
            if (!string.IsNullOrWhiteSpace(r.Description)) sb.AppendLine($"  Desc: {r.Description}");
            sb.AppendLine($"  Params: [{paramStr}] | Success Rate: {successRate} ({r.SuccessCount} pass / {r.FailureCount} fail)");
            if (r.LastRunAt != null) sb.AppendLine($"  Last Run: {r.LastRunAt:yyyy-MM-dd HH:mm}");
        }
        return Task.FromResult(Text(sb.ToString().TrimEnd()));
    }

    [McpServerTool(Name = "computer_screen_memory"), Description(
        "Query the persistent desktop state and navigation memory: known screens, previously seen elements, and navigation transitions for an application.")]
    public CallToolResult ScreenMemory(
        [Description("action: 'summary' | 'screens' | 'elements' | 'path' | 'find'")] string action = "summary",
        [Description("process/app name (defaults to active foreground app)")] string? app = null,
        [Description("target element name to find across all screens")] string? query = null,
        [Description("starting screen for path search")] string? fromScreen = null,
        [Description("destination screen for path search")] string? toScreen = null,
        CancellationToken ct = default)
    {
        var targetApp = app;
        if (string.IsNullOrWhiteSpace(targetApp))
            targetApp = _s.Rt.ForegroundWindow()?.ProcessName;

        var act = (action ?? "summary").ToLowerInvariant();
        switch (act)
        {
            case "find":
            {
                if (string.IsNullOrWhiteSpace(query))
                    return Error(OutcomeKind.Malformed, "query is required for 'find'");
                if (string.IsNullOrWhiteSpace(targetApp))
                    return Error(OutcomeKind.Malformed, "app is required or a foreground window must exist");
                var sug = _s.Memory.SuggestRecovery(query, null, targetApp);
                var matches = _s.Memory.GetKnownElements(targetApp)
                    .Where(e => e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(e => new
                    {
                        name = e.Name,
                        role = e.Role,
                        screen = e.ScreenId,
                        presence = e.Presence.ToString(),
                        lastSeen = e.LastSeen,
                    }).ToList();
                return Json(new
                {
                    app = targetApp,
                    matches = matches,
                    suggestion = sug != null ? new
                    {
                        targetScreen = sug.TargetScreen,
                        hint = sug.NavigationHint,
                        path = sug.Path,
                    } : null,
                    provenance = Provenance("uia")
                });
            }
            case "path":
            {
                if (string.IsNullOrWhiteSpace(targetApp))
                    return Error(OutcomeKind.Malformed, "app is required or a foreground window must exist");
                if (string.IsNullOrWhiteSpace(fromScreen) || string.IsNullOrWhiteSpace(toScreen))
                    return Error(OutcomeKind.Malformed, "fromScreen and toScreen are required for 'path'");
                var path = _s.Memory.FindNavigationPath(targetApp, fromScreen, toScreen);
                return Json(new
                {
                    app = targetApp,
                    from = fromScreen,
                    to = toScreen,
                    reachable = path != null,
                    steps = path?.Select(p => p.ActionDescription).ToList() ?? [],
                    provenance = Provenance("uia")
                });
            }
            case "screens":
            {
                if (string.IsNullOrWhiteSpace(targetApp))
                    return Error(OutcomeKind.Malformed, "app is required or a foreground window must exist");
                var screens = _s.Memory.GetScreens(targetApp);
                return Json(new
                {
                    app = targetApp,
                    currentScreen = _s.Memory.GetCurrentScreen(targetApp),
                    screens = screens.Select(s => new
                    {
                        screenId = s.ScreenId,
                        title = s.TitlePattern,
                        landmarks = s.Landmarks,
                        lastActive = s.LastActive,
                    }).ToList(),
                    provenance = Provenance("uia")
                });
            }
            case "elements":
            {
                if (string.IsNullOrWhiteSpace(targetApp))
                    return Error(OutcomeKind.Malformed, "app is required or a foreground window must exist");
                var elements = _s.Memory.GetKnownElements(targetApp, fromScreen);
                return Json(new
                {
                    app = targetApp,
                    screen = fromScreen,
                    elements = elements.Select(e => new
                    {
                        name = e.Name,
                        role = e.Role,
                        screen = e.ScreenId,
                        presence = e.Presence.ToString(),
                    }).Take(60).ToList(),
                    provenance = Provenance("uia")
                });
            }
            case "summary":
            default:
            {
                var summary = _s.Memory.GetSummary(targetApp);
                return Json(summary);
            }
        }
    }
}
