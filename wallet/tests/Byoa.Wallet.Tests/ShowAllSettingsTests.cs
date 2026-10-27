using Byoa.Wallet.Desktop;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The "Show all settings" switch: the short tab list by default (and everywhere needs no second change), every tab
/// when it is on, and the setting itself across a restart and from an old settings file.
/// </summary>
public class ShowAllSettingsTests
{
    [Fact]
    public void ByDefaultOnlyTheSimpleTabsShow()
    {
        // Everywhere (dictation and the check in every program) is Windows only for now: no tab for it on a Mac
        string[] simple = OperatingSystem.IsWindows() ? ["sites", "sources", "everywhere", "activity"] : ["sites", "sources", "activity"];
        Assert.Equal(simple, SettingsWindow.VisiblePages(showAll: false).Select(p => p.Id).ToArray());
        var all = SettingsWindow.VisiblePages(showAll: true).Select(p => p.Id).ToArray();
        Assert.True(all.Length >= 8);                                     // guard, Skills, Limits, My MCP, help…
        Assert.Equal(all, SettingsWindow.VisiblePages(showAll: true).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void ANewTabAppearsInTheShortListByAddingItToPagesAlone()
    {
        // The Everywhere tab is added by another agent with one line in Pages. The short list picks it up by id alone.
        var withEverywhere = SettingsWindow.VisiblePages(showAll: false,
        [
            ("sites", "Sites", "g"),
            ("sources", "My models", "m"),
            ("myagents", "My agents", "a"),
            ("guard", "Agent guard", "c"),
            ("everywhere", "Everywhere", "e"),
            ("activity", "Activity", "l"),
        ]);

        Assert.Equal(["sites", "sources", "everywhere", "activity"], withEverywhere.Select(p => p.Id).ToArray());   // the tab list's own order
        Assert.Equal(6, SettingsWindow.VisiblePages(showAll: true,
        [
            ("sites", "Sites", "g"),
            ("sources", "My models", "m"),
            ("myagents", "My agents", "a"),
            ("guard", "Agent guard", "c"),
            ("everywhere", "Everywhere", "e"),
            ("activity", "Activity", "l"),
        ]).Count);
    }

    [Fact]
    public void TheSwitchSurvivesARestartAndAnOldFileLoadsUnchanged()
    {
        var path = Path.Combine(TestFolder.New(), "settings.json");
        // an old settings file: it knows no showAllSettings at all
        File.WriteAllText(path, """{"version": 1, "sites": [], "sources": [], "chains": []}""");

        var old = new SettingsStore(path);
        Assert.False(old.Read(s => s.ShowAllSettings));                   // the default stays off
        old.Update(s => s.ShowAllSettings = true);
        old.Update(s => s.Sites.Add(new SiteRecord { Origin = "https://one.example" }));

        var again = new SettingsStore(path);                              // a restart reads it back
        Assert.True(again.Read(s => s.ShowAllSettings));
        Assert.Single(again.Read(s => s.Sites));
    }
}
