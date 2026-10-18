using System.Diagnostics;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The warm-up when a site connects: each model it will use does its simplest job once for real and the connection waits for it. On by
/// default, can be turned off in the settings; a link's wait ends in time for the link's one-time code.
/// </summary>
public class WarmUpTests : IDisposable
{
    const string Site = "https://geo.example.org";

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly TimeSpan _savedPreparation = WalletEngine.PreparationTimeout;
    readonly TimeSpan _savedBudget = WalletEngine.LinkPreparationBudget;
    readonly TimeSpan _savedMinimum = WalletEngine.MinPreparationWait;

    public WarmUpTests() => _log = new ActivityLog(null, _time.Now);

    public void Dispose()
    {
        WalletEngine.PreparationTimeout = _savedPreparation;
        WalletEngine.LinkPreparationBudget = _savedBudget;
        WalletEngine.MinPreparationWait = _savedMinimum;
    }

    WalletEngine EngineWith(WarmingSource source) => SpeechRoutes.Engine(_store, [source], _log, _ui, _time);

    static WireMessage Request() => Clips.Request(Site, click: true, Clips.GeoManifest);

    [Fact]
    public void TheOptionIsOnByDefault()
    {
        Assert.True(new WalletSettings().WarmUpOnConnect);

        // a settings file from before the option existed gets it on too
        var path = Path.Combine(Path.GetTempPath(), "byoa-warmup-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"version\":1}");
        try
        {
            Assert.True(new SettingsStore(path).Read(s => s.WarmUpOnConnect));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AGrantWaitsForTheModelsJobAndTheFirstCallIsNotSlow()
    {
        var model = new WarmingSource("local", ["ru", "sk"]) { WarmTime = TimeSpan.FromMilliseconds(600), Answer = "francia" };
        var engine = EngineWith(model);

        var watch = Stopwatch.StartNew();
        var grant = Assert.IsType<JsonObject>(await engine.HandleAsync(Request()));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(500), watch.Elapsed.ToString());      // the connection waited for the model
        Assert.NotEmpty(model.Warmed);

        watch.Restart();
        var answer = await engine.HandleAsync(Clips.Call(Site, grant["grantId"]!.GetValue<string>()));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(400), watch.Elapsed.ToString());       // the first real request met a loaded model
        Assert.True(answer is JsonObject o && o["ok"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task TheModelOfTwoLanguagesIsMadeToDoItsJobOnce()
    {
        var model = new WarmingSource("local", ["ru", "sk"]) { WarmTime = TimeSpan.FromMilliseconds(300) };
        await EngineWith(model).HandleAsync(Request());
        Assert.Single(model.Warmed);            // ru and sk go to the same source: one warm-up job, shared
    }

    [Fact]
    public async Task WithTheOptionOffNothingIsWarmedAndNothingIsWaitedFor()
    {
        _store.Update(s => s.WarmUpOnConnect = false);
        var model = new WarmingSource("local", ["ru", "sk"]) { WarmTime = TimeSpan.FromSeconds(3) };
        var engine = EngineWith(model);

        var watch = Stopwatch.StartNew();
        Assert.NotNull(await engine.HandleAsync(Request()));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), watch.Elapsed.ToString());
        Assert.Empty(model.Warmed);
        Assert.Empty(_ui.Preparing);
        engine.WarmAllowedSites();              // not at start either
        await Task.Delay(100);
        Assert.Empty(model.Warmed);
    }

    [Fact]
    public async Task AModelThatNeverGetsReadyDoesNotHoldTheSiteForever()
    {
        WalletEngine.PreparationTimeout = TimeSpan.FromMilliseconds(700);
        var model = new WarmingSource("local", ["ru", "sk"]) { WarmTime = TimeSpan.FromSeconds(30) };
        var watch = Stopwatch.StartNew();
        Assert.NotNull(await EngineWith(model).HandleAsync(Request()));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.6, 5);
        var info = Assert.Single(_ui.Infos);
        Assert.Contains("taking longer than 1 seconds", info.Text);
    }

    [Fact]
    public async Task ALinksWaitEndsInTimeForItsOneTimeCode()
    {
        WalletEngine.LinkPreparationBudget = TimeSpan.FromMilliseconds(500);
        WalletEngine.MinPreparationWait = TimeSpan.FromMilliseconds(300);
        var model = new WarmingSource("local", ["ru", "sk"]) { WarmTime = TimeSpan.FromSeconds(30) };
        var engine = EngineWith(model);

        var watch = Stopwatch.StartNew();
        var answer = await engine.RequestViaLinkAsync(Site, JsonNode.Parse(Clips.GeoManifest)!.AsObject(), userActivation: true);
        Assert.NotNull(answer);                                    // the visitor allowed: the connection goes on, the model keeps loading
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.25, 5);       // the page's own timeout (PreparationTimeout, minutes) was not what ended it
        Assert.Single(_ui.Infos);
    }
}
