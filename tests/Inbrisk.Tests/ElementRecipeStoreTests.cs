using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

public class ElementRecipeStoreTests
{
    [Fact]
    public void ElementRecipeStore_CrossProcessLookup_WorksAfterMintingProcessExits()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"recipe_store_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Simulate Process A (PID 88888) minting an element
            int simulatedPidA = 88888;
            var elementId = $"uia_{simulatedPidA}_42";
            var fakeFileA = Path.Combine(tempDir, $"elements-{simulatedPidA}.json");

            // Write persisted element to fakeFileA
            var recipe = new ReResolveRecipe(1, 100, "Calc", Role.Button, "Five", "btn5", Array.Empty<AncestryStep>(), new RectPx(10, 10, 50, 50));
            var el = new UiElement(elementId, BackendId.Uia, Role.Button, "Five", new RectPx(10, 10, 50, 50),
                new[] { "click" }, new Dictionary<string, object?>(), new ElementHandle(BackendId.Uia, elementId, recipe), 1, 100);

            // Save using a store instance configured to simulate Pid A's file
            var storeA = new ElementRecipeStore(tempDir);
            storeA.Put(el);
            storeA.Flush();

            // Also explicitly copy to elements-88888.json to simulate another process with PID 88888 having written it
            var ownWrittenFile = Path.Combine(tempDir, $"elements-{Environment.ProcessId}.json");
            Assert.True(File.Exists(ownWrittenFile));
            File.Copy(ownWrittenFile, fakeFileA, overwrite: true);

            // Now Process B (another process / new store instance) starts up and wants to invoke uia_88888_42
            var storeB = new ElementRecipeStore(tempDir);
            var handle = storeB.Lookup(elementId);

            Assert.NotNull(handle);
            Assert.Equal(BackendId.Uia, handle!.Backend);
            Assert.Equal("Five", handle.Recipe.Name);
            Assert.Equal("btn5", handle.Recipe.AutomationId);

            // Verify file is still present and not deleted on startup or exit
            Assert.True(File.Exists(fakeFileA));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ElementRecipeStore_Pruning_PreservesRecentDeadProcessFiles_AndPrunesOldDeadFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"recipe_prune_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Dead PID 77771, written recently (e.g. 1 minute ago) -> must be preserved
            var recentDeadFile = Path.Combine(tempDir, "elements-77771.json");
            File.WriteAllText(recentDeadFile, "{}");
            File.SetLastWriteTimeUtc(recentDeadFile, DateTime.UtcNow.AddMinutes(-2));

            // Dead PID 77772, written 40 minutes ago (exceeded 30m TTL) -> must be pruned
            var oldDeadFile = Path.Combine(tempDir, "elements-77772.json");
            File.WriteAllText(oldDeadFile, "{}");
            File.SetLastWriteTimeUtc(oldDeadFile, DateTime.UtcNow.AddMinutes(-40));

            // Active PID (current process), written 40 minutes ago -> must NEVER be pruned because process is alive
            var aliveFile = Path.Combine(tempDir, $"elements-{Environment.ProcessId}.json");
            File.WriteAllText(aliveFile, "{}");
            File.SetLastWriteTimeUtc(aliveFile, DateTime.UtcNow.AddMinutes(-40));

            var store = new ElementRecipeStore(tempDir);
            store.PruneNow();

            Assert.True(File.Exists(recentDeadFile), "Recent dead process file must remain for subsequent CLI commands.");
            Assert.False(File.Exists(oldDeadFile), "Expired dead process file must be pruned.");
            Assert.True(File.Exists(aliveFile), "Active process file must never be pruned.");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
