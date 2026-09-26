using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

public sealed class DesktopStateMemoryTests
{
    private static UiElement MakeEl(string id, string name, Role role, bool isSelected = false)
    {
        var props = new Dictionary<string, object?>
        {
            ["selected"] = isSelected,
            ["enabled"] = true,
        };
        var bounds = new RectPx(0, 0, 100, 30);
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(1, 100, "app", role, name, id, Array.Empty<AncestryStep>(), bounds));
        return new UiElement(id, BackendId.Uia, role, name, bounds,
            ["invoke"], props, handle, 1, 100);
    }

    [Fact]
    public void InferScreen_FromSelectedTab_ReturnsTabName()
    {
        var elements = new[]
        {
            MakeEl("tab1", "Ana Sayfa", Role.TabItem, isSelected: false),
            MakeEl("tab2", "Kitaplığın", Role.TabItem, isSelected: true),
            MakeEl("tab3", "Arama", Role.TabItem, isSelected: false),
        };

        var memory = new DesktopStateMemory(Path.Combine(Path.GetTempPath(), $"mem-{Guid.NewGuid():N}.json"));
        var screen = memory.InferScreen("spotify", "Spotify Free", elements);

        Assert.Equal("Kitaplığın", screen);
    }

    [Fact]
    public void InferScreen_FromWindowTitle_ReturnsSubScreen()
    {
        var memory = new DesktopStateMemory(Path.Combine(Path.GetTempPath(), $"mem-{Guid.NewGuid():N}.json"));
        var screen = memory.InferScreen("code", "InbriskTools.cs - inbrisk - Visual Studio Code", Array.Empty<UiElement>());

        Assert.Equal("InbriskTools.cs", screen);
    }

    [Fact]
    public void RecordObservation_TracksCurrentlyVisible_AndMarksOlderPreviouslySeen()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mem-{Guid.NewGuid():N}.json");
        var memory = new DesktopStateMemory(tempFile);

        // Screen 1: Home
        var homeElements = new[]
        {
            MakeEl("el_home1", "Ana Sayfa", Role.TabItem, isSelected: true),
            MakeEl("btn_play", "Çal", Role.Button),
        };
        var screen1 = memory.RecordObservation(100, "spotify", "Spotify - Ana Sayfa", homeElements);
        Assert.Equal("Ana Sayfa", screen1);

        var elementsHome = memory.GetKnownElements("spotify", "Ana Sayfa");
        Assert.Contains(elementsHome, e => e.Name == "Çal" && e.Presence == ElementPresence.CurrentlyVisible);

        // Screen 2: Library
        var libElements = new[]
        {
            MakeEl("el_lib1", "Kitaplığın", Role.TabItem, isSelected: true),
            MakeEl("list_target", "Hedef Liste", Role.ListItem),
        };
        var screen2 = memory.RecordObservation(100, "spotify", "Spotify - Kitaplığın", libElements);
        Assert.Equal("Kitaplığın", screen2);

        // "Hedef Liste" is CurrentlyVisible on Kitaplığın
        var elementsLib = memory.GetKnownElements("spotify", "Kitaplığın");
        Assert.Contains(elementsLib, e => e.Name == "Hedef Liste" && e.Presence == ElementPresence.CurrentlyVisible);

        // "Çal" from Home screen is now marked PreviouslySeen
        var allElements = memory.GetKnownElements("spotify");
        var playBtn = Assert.Single(allElements, e => e.Name == "Çal");
        Assert.Equal(ElementPresence.PreviouslySeen, playBtn.Presence);
        Assert.Equal("Ana Sayfa", playBtn.ScreenId);
    }

    [Fact]
    public void RecordTransition_And_FindNavigationPath_FindsShortestPath()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mem-{Guid.NewGuid():N}.json");
        var memory = new DesktopStateMemory(tempFile);

        // Transitions: Home -> Search, Search -> Library, Library -> PlaylistDetail
        memory.RecordTransition("spotify", "Home", "Search", "click 'Arama'", targetName: "Arama");
        memory.RecordTransition("spotify", "Search", "Library", "click 'Kitaplığın'", targetName: "Kitaplığın");
        memory.RecordTransition("spotify", "Library", "PlaylistDetail", "click 'Favorilerim'", targetName: "Favorilerim");

        // Path from Home to PlaylistDetail (Home -> Search -> Library -> PlaylistDetail)
        var path = memory.FindNavigationPath("spotify", "Home", "PlaylistDetail");
        Assert.NotNull(path);
        Assert.Equal(3, path.Count);
        Assert.Equal("click 'Arama'", path[0].ActionDescription);
        Assert.Equal("click 'Kitaplığın'", path[1].ActionDescription);
        Assert.Equal("click 'Favorilerim'", path[2].ActionDescription);

        // Direct path from Search to Library
        var direct = memory.FindNavigationPath("spotify", "Search", "Library");
        Assert.NotNull(direct);
        Assert.Single(direct);
        Assert.Equal("click 'Kitaplığın'", direct[0].ActionDescription);
    }

    [Fact]
    public void SuggestRecovery_WhenElementOnDifferentScreen_ReturnsNavigationHint()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mem-{Guid.NewGuid():N}.json");
        var memory = new DesktopStateMemory(tempFile);

        // Discover Library screen and target playlist
        var libElements = new[]
        {
            MakeEl("tab_lib", "Kitaplığın", Role.TabItem, isSelected: true),
            MakeEl("item_rock", "Türkçe Rock", Role.ListItem),
        };
        memory.RecordObservation(100, "spotify", "Spotify - Kitaplığın", libElements);

        // Navigate to Search screen
        var searchElements = new[]
        {
            MakeEl("tab_search", "Arama", Role.TabItem, isSelected: true),
            MakeEl("txt_search", "Ne dinlemek istiyorsun?", Role.Edit),
        };
        memory.RecordObservation(100, "spotify", "Spotify - Arama", searchElements);
        memory.RecordTransition("spotify", "Arama", "Kitaplığın", "click 'Kitaplığın'", targetName: "Kitaplığın");

        // While on "Arama" screen, agent searches for "Türkçe Rock"
        var suggestion = memory.SuggestRecovery("Türkçe Rock", "ListItem", "spotify", currentScreen: "Arama");

        Assert.NotNull(suggestion);
        Assert.Equal("Kitaplığın", suggestion.TargetScreen);
        Assert.Equal("Arama", suggestion.CurrentScreen);
        Assert.Contains("click 'Kitaplığın'", suggestion.NavigationHint);
    }

    [Fact]
    public void Persistence_Roundtrip_RestoresScreensAndTransitions()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mem-{Guid.NewGuid():N}.json");
        try
        {
            var memory1 = new DesktopStateMemory(tempFile);
            memory1.RecordObservation(100, "notepad", "Doc.txt - Notepad", [
                MakeEl("edit_doc", "Text Editor", Role.Document),
            ]);
            memory1.RecordTransition("notepad", "Editor", "Settings", "click 'Ayarlar'");
            memory1.Flush();

            // Load into fresh instance
            var memory2 = new DesktopStateMemory(tempFile);
            var screens = memory2.GetScreens("notepad");
            var transitions = memory2.GetTransitions("notepad");

            Assert.NotEmpty(screens);
            Assert.NotEmpty(transitions);
            Assert.Equal("click 'Ayarlar'", transitions[0].ActionDescription);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
