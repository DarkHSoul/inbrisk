using System.Runtime.CompilerServices;

namespace Inbrisk.Tests;

/// <summary>
/// Runs once before any test in this assembly (module initializer). Points the
/// Inbrisk data directory at a throwaway temp path so tests can never read or
/// write the real %LocalAppData%\inbrisk store.
///
/// Regression guard: recipe tests used to construct McpSession without any
/// data-dir override, so TaskRecipeStore defaulted to the *real* user profile
/// and wrote test fixtures (test_recipe_1, test_recipe_conflict, ...) into the
/// user's task-recipes directory on every `dotnet test` run.
/// </summary>
internal static class TestEnvironmentInit
{
    private static string? _tempDir;

    [ModuleInitializer]
    internal static void Init()
    {
        // Respect an explicit override if a runner already provided one.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INBRISK_DATA_DIR")))
            return;

        _tempDir = Path.Combine(Path.GetTempPath(), "inbrisk-tests-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(_tempDir); } catch { }
        Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", _tempDir);
    }
}
