using System;
using System.Collections.Generic;

namespace Inbrisk.Core;

public sealed record RecipeParameter(
    string Name,
    string? Description = null,
    string? DefaultValue = null);

public sealed record TaskRecipeDefinition(
    string Name,
    string? Description,
    string? App,
    IReadOnlyList<RecipeParameter> Parameters,
    IReadOnlyList<string> Preconditions,
    string StepsJson,
    IReadOnlyList<string> Postconditions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt = null,
    int SuccessCount = 0,
    int FailureCount = 0);

/// <summary>
/// Abstraction for storing and executing semantic task recipes.
/// Enables platform and MCP layers to share recipe knowledge without circular assembly dependencies.
/// </summary>
public interface ITaskRecipeStore
{
    void Save(TaskRecipeDefinition recipe);
    TaskRecipeDefinition? Get(string name);
    IReadOnlyList<TaskRecipeDefinition> List();
    void RecordRun(string name, bool success);
}
