using Byoa.Wallet.Desktop;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The settings window shows every tab at all times (the "Show all settings" switch is gone), and a settings file that still has the
/// old switch loads as before.
/// </summary>
public class ShowAllSettingsTests
{
    [Fact]
    public void EveryTabIsAlwaysThere()
    {
        var ids = SettingsWindow.VisiblePages().Select(p => p.Id).ToArray();
        foreach (var id in new[] { "sites", "sources", "myagents", "guard", "skills", "limits", "activity", "connect" })
            Assert.Contains(id, ids);
        // Everywhere (dictation and the check in every program) is Windows only for now: no tab for it elsewhere
        Assert.Equal(OperatingSystem.IsWindows(), ids.Contains("everywhere"));
        Assert.True(ids.Length >= 8);
    }

    [Fact]
    public void AnOldFileWithTheSwitchStillLoads()
    {
        var path = Path.Combine(TestFolder.New(), "settings.json");
        File.WriteAllText(path, """{"version": 1, "showAllSettings": true, "sites": [], "sources": [], "chains": []}""");

        var store = new SettingsStore(path);
        Assert.True(store.Read(s => s.ShowAllSettings));                   // read, and ignored by the window
        store.Update(s => s.Sites.Add(new SiteRecord { Origin = "https://one.example" }));
        Assert.Single(new SettingsStore(path).Read(s => s.Sites));
    }
}
