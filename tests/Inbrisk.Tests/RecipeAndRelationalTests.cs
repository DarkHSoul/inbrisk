using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

public class RecipeAndRelationalTests
{
    [Fact]
    public void RelationalInspector_Extracts_RowsAndActions_Accurately()
    {
        var listBounds = new RectPx(0, 0, 500, 300);
        var row1Bounds = new RectPx(10, 10, 480, 40);
        var row2Bounds = new RectPx(10, 60, 480, 40);

        var list = new UiElement("list_1", BackendId.Uia, Role.List, "Songs", listBounds,
            Array.Empty<string>(), new Dictionary<string, object?>(),
            new ElementHandle(BackendId.Uia, "r0", new ReResolveRecipe(1, 10, "App", Role.List, "Songs", "list1", Array.Empty<AncestryStep>(), listBounds)), 1, 10);

        var row1Ancestry = new[] { new AncestryStep(Role.List, "Songs", "list1", 0) };
        var row1 = new UiElement("row_1", BackendId.Uia, Role.ListItem, "Track 1", row1Bounds,
            new[] { "Select" }, new Dictionary<string, object?>(),
            new ElementHandle(BackendId.Uia, "r1", new ReResolveRecipe(1, 10, "App", Role.ListItem, "Track 1", "row1", row1Ancestry, row1Bounds)), 1, 10);

        var row1ChildAncestry = new[] { new AncestryStep(Role.List, "Songs", "list1", 0), new AncestryStep(Role.ListItem, "Track 1", "row1", 0) };
        var btnPlay = new UiElement("btn_play_1", BackendId.Uia, Role.Button, "Play", new RectPx(20, 15, 30, 30),
            new[] { "Invoke" }, new Dictionary<string, object?>(),
            new ElementHandle(BackendId.Uia, "r1_play", new ReResolveRecipe(1, 10, "App", Role.Button, "Play", "btnPlay1", row1ChildAncestry, new RectPx(20, 15, 30, 30))), 1, 10);

        var txtArtist = new UiElement("txt_art_1", BackendId.Uia, Role.Text, "Queen", new RectPx(60, 15, 100, 20),
            Array.Empty<string>(), new Dictionary<string, object?>(),
            new ElementHandle(BackendId.Uia, "r1_txt", new ReResolveRecipe(1, 10, "App", Role.Text, "Queen", "txtArt1", row1ChildAncestry, new RectPx(60, 15, 100, 20))), 1, 10);

        var row2Ancestry = new[] { new AncestryStep(Role.List, "Songs", "list1", 0) };
        var row2 = new UiElement("row_2", BackendId.Uia, Role.ListItem, "Track 2", row2Bounds,
            new[] { "Select" }, new Dictionary<string, object?>(),
            new ElementHandle(BackendId.Uia, "r2", new ReResolveRecipe(1, 10, "App", Role.ListItem, "Track 2", "row2", row2Ancestry, row2Bounds)), 1, 10);

        var row2ChildAncestry = new[] { new AncestryStep(Role.List, "Songs", "list1", 0), new AncestryStep(Role.ListItem, "Track 2", "row2", 1) };
        var btnMenu = new UiElement("btn_menu_2", BackendId.Uia, Role.MenuItem, "Options", new RectPx(450, 65, 30, 30),
            new[] { "Invoke" }, new Dictionary<string, object?>(),
            new ElementHandle(BackendId.Uia, "r2_menu", new ReResolveRecipe(1, 10, "App", Role.MenuItem, "Options", "btnMenu2", row2ChildAncestry, new RectPx(450, 65, 30, 30))), 1, 10);

        var allElements = new[] { list, row1, btnPlay, txtArtist, row2, btnMenu };

        var rows = RelationalInspector.ExtractRows(allElements);

        Assert.Equal(2, rows.Count);

        var r1 = rows[0];
        Assert.Equal("row_1", r1.ElementId);
        Assert.Equal("Track 1", r1.Title);
        Assert.Contains("Queen", r1.Details);
        Assert.Single(r1.Actions);
        Assert.Equal("btn_play_1", r1.Actions[0].ElementId);
        Assert.Equal("Play", r1.Actions[0].Name);

        var r2 = rows[1];
        Assert.Equal("row_2", r2.ElementId);
        Assert.Equal("Track 2", r2.Title);
        Assert.Single(r2.Actions);
        Assert.Equal("btn_menu_2", r2.Actions[0].ElementId);

        var text = RelationalInspector.FormatRowsAsText(rows);
        Assert.Contains("[row_1] ListItem: \"Track 1\" (Queen)", text);
        Assert.Contains("[row_2] ListItem: \"Track 2\"", text);
        Assert.Contains("Invoke(btn_play_1: \"Play\")", text);
    }

    [Fact]
    public void TaskRecipeStore_Persists_And_RecordsRunHistory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk_recipetest_{Guid.NewGuid():N}");
        try
        {
            var store = new TaskRecipeStore(tempDir);
            Assert.Empty(store.List());

            var recipe = new TaskRecipeDefinition(
                Name: "add_to_playlist",
                Description: "Add song to Spotify playlist",
                App: "Spotify",
                Parameters: new[]
                {
                    new RecipeParameter("songTitle", "Title of track"),
                    new RecipeParameter("targetPlaylist", "Name of playlist")
                },
                Preconditions: new[] { "Spotify window open" },
                StepsJson: "[{\"action\":\"find\",\"target\":{\"name\":\"{{songTitle}}\"}}]",
                Postconditions: new[] { "Track appears in targetPlaylist" },
                CreatedAt: DateTimeOffset.UtcNow);

            store.Save(recipe);

            var retrieved = store.Get("add_to_playlist");
            Assert.NotNull(retrieved);
            Assert.Equal("Spotify", retrieved!.App);
            Assert.Equal(2, retrieved.Parameters.Count);
            Assert.Equal(0, retrieved.SuccessCount);

            // Record runs
            store.RecordRun("add_to_playlist", true);
            store.RecordRun("add_to_playlist", true);
            store.RecordRun("add_to_playlist", false);

            var updated = store.Get("add_to_playlist");
            Assert.NotNull(updated);
            Assert.Equal(2, updated!.SuccessCount);
            Assert.Equal(1, updated.FailureCount);
            Assert.NotNull(updated.LastRunAt);

            var all = store.List();
            Assert.Single(all);
            Assert.Equal("add_to_playlist", all[0].Name);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TaskRecipeStore_NameSanitization_IsConsistent_BetweenSaveAndGet()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk_recipetest_{Guid.NewGuid():N}");
        try
        {
            var store = new TaskRecipeStore(tempDir);
            var recipe = new TaskRecipeDefinition(
                Name: "  Test Recipe  ",
                Description: "trim probe",
                App: "Notepad",
                Parameters: Array.Empty<RecipeParameter>(),
                Preconditions: Array.Empty<string>(),
                StepsJson: "[]",
                Postconditions: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.UtcNow);
            store.Save(recipe);

            // Trim inconsistency guard: a name saved padded and later looked
            // up trimmed (and vice versa) must resolve to the same file.
            Assert.NotNull(store.Get("Test Recipe"));
            Assert.NotNull(store.Get("test recipe"));
            Assert.Null(store.Get("a different recipe"));

            // PathFor must agree with the file Save actually wrote.
            Assert.Equal(store.PathFor(" Test Recipe "), store.PathFor("test recipe"));
            Assert.Equal(
                Path.Combine(tempDir, "test recipe.json"),
                store.PathFor(" Test Recipe "));

            // Case-only variants deliberately collapse to one file
            // (case-insensitive Windows filesystems would merge them anyway).
            var renamed = recipe with { Name = "TEST RECIPE" };
            store.Save(renamed);
            Assert.Single(store.List().Where(r => r.Name is "TEST RECIPE" or " Test Recipe "));
            Assert.NotNull(store.Get("test recipe"));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TaskRecipeStore_Rejects_NamesWithNoUsableFilenameCharacters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk_recipetest_{Guid.NewGuid():N}");
        try
        {
            var store = new TaskRecipeStore(tempDir);
            var recipe = new TaskRecipeDefinition(
                Name: "///",
                Description: "placeholder-only name",
                App: "Notepad",
                Parameters: Array.Empty<RecipeParameter>(),
                Preconditions: Array.Empty<string>(),
                StepsJson: "[]",
                Postconditions: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.UtcNow);

            // "///" would previously be persisted as "___.json", colliding
            // silently with "==" or "***" — reject instead of writing junk.
            Assert.Throws<ArgumentException>(() => store.Save(recipe));
            Assert.Empty(store.List());

            // Placeholder-only lookups are null rather than probing a junk file.
            Assert.Null(store.Get("==="));
            Assert.Null(store.Get("   "));
            Assert.Null(store.Get(""));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TaskRecipeStore_List_SurfacesUnreadableRecipeFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk_recipetest_{Guid.NewGuid():N}");
        try
        {
            var store = new TaskRecipeStore(tempDir);
            var good = new TaskRecipeDefinition(
                Name: "good_recipe",
                Description: "loadable",
                App: "Notepad",
                Parameters: Array.Empty<RecipeParameter>(),
                Preconditions: Array.Empty<string>(),
                StepsJson: "[]",
                Postconditions: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.UtcNow);
            store.Save(good);

            var goodPath = store.PathFor("good_recipe");
            Assert.True(File.Exists(goodPath));

            // Poison a file so List's per-file catch fires: the good recipe
            // must still be listed and the poisoned one must exist on disk
            // (visible for diagnostics) but simply not deserialize.
            var poisoned = Path.Combine(tempDir, "poisoned_recipe.json");
            File.WriteAllText(poisoned, "{ not valid json");
            Assert.True(File.Exists(poisoned));

            var listed = store.List().Select(r => r.Name).ToList();
            Assert.Contains("good_recipe", listed);
            Assert.DoesNotContain("poisoned_recipe", listed);
            Assert.Null(store.Get("poisoned_recipe"));

            // Cleanup the poisoned file so the directory delete succeeds.
            File.Delete(poisoned);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
