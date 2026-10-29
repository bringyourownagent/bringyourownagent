using Byoa.Wallet.Desktop;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Tests;

/// <summary>The installed wallet's registration for the current Windows user, and "Check for updates".</summary>
public class InstallerTests
{
    const string HostKey = @"Software\Google\Chrome\NativeMessagingHosts\org.bringyourownagent.wallet";
    const string ChromiumKey = @"Software\Chromium\NativeMessagingHosts\org.bringyourownagent.wallet";
    const string ProtocolKey = @"Software\Classes\bringyourownagent";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    sealed class FakeRegistry : IRegistryStore
    {
        public readonly Dictionary<(string Key, string Name), string> Values = [];
        public int Writes;

        public void SetValue(string key, string? name, string value)
        {
            Values[(key, name ?? "")] = value;
            Writes++;
        }

        public string? GetValue(string key, string? name) => Values.TryGetValue((key, name ?? ""), out var value) ? value : null;

        public void DeleteTree(string key)
        {
            foreach (var entry in Values.Keys.Where(k => k.Key == key || k.Key.StartsWith(key + "\\", StringComparison.Ordinal)).ToList()) Values.Remove(entry);
        }

        public void DeleteValue(string key, string name) => Values.Remove((key, name));
    }

    /// <summary>A folder laid out like an installed wallet: the programs and the extension with its key.</summary>
    static string InstalledFolder(bool withExtension = true)
    {
        var folder = Path.Combine(Path.GetTempPath(), "byoa-installed-" + Guid.NewGuid().ToString("N")[..10], "current");
        Directory.CreateDirectory(Path.Combine(folder, "extension"));
        File.WriteAllText(Path.Combine(folder, "Byoa.Wallet.exe"), "");
        File.WriteAllText(Path.Combine(folder, "byoa-host.exe"), "");
        if (!withExtension) return folder;
        // any valid base64 key gives an extension ID; the real one is fixed by the "key" of wallet\extension\manifest.json
        File.WriteAllText(Path.Combine(folder, "extension", "manifest.json"), "{\"name\":\"x\",\"key\":\"" + Convert.ToBase64String(new byte[64]) + "\"}");
        return folder;
    }

    [Fact]
    public void AnInstallRegistersTheHostForBothBrowsersAndTheLinks()
    {
        var folder = InstalledFolder();
        var registry = new FakeRegistry();
        new WindowsRegistration(registry).Apply(folder);

        var manifest = Path.Combine(folder, "org.bringyourownagent.wallet.json");
        Assert.True(File.Exists(manifest));
        Assert.Equal(manifest, registry.GetValue(HostKey, null));
        Assert.Equal(manifest, registry.GetValue(ChromiumKey, null));
        var json = File.ReadAllText(manifest);
        Assert.Contains(Path.Combine(folder, "byoa-host.exe").Replace("\\", "\\\\"), json);
        Assert.Contains("chrome-extension://", json);
        Assert.Equal($"\"{Path.Combine(folder, "Byoa.Wallet.exe")}\" --url \"%1\"", registry.GetValue(ProtocolKey + @"\shell\open\command", null));
        Assert.Equal("", registry.GetValue(ProtocolKey, "URL Protocol"));
        Assert.Equal($"\"{Path.Combine(folder, "Byoa.Wallet.exe")}\",0", registry.GetValue(ProtocolKey + @"\DefaultIcon", null));
    }

    [Fact]
    public void RegisteringAgainWritesNothingNew()
    {
        var folder = InstalledFolder();
        var registry = new FakeRegistry();
        var registration = new WindowsRegistration(registry);
        registration.Apply(folder);
        var writes = registry.Writes;
        var manifestTime = File.GetLastWriteTimeUtc(Path.Combine(folder, "org.bringyourownagent.wallet.json"));

        registration.Apply(folder);

        Assert.Equal(writes, registry.Writes);                                // every start of the wallet runs this: it must not churn
        Assert.Equal(manifestTime, File.GetLastWriteTimeUtc(Path.Combine(folder, "org.bringyourownagent.wallet.json")));
    }

    [Fact]
    public void ARegistrationThatPointsElsewhereIsPutRight()
    {
        var folder = InstalledFolder();
        var registry = new FakeRegistry();
        registry.Values[(HostKey, "")] = @"C:\old\place\host.json";
        registry.Values[(ProtocolKey + @"\shell\open\command", "")] = "\"C:\\old\\Byoa.Wallet.exe\" --url \"%1\"";

        new WindowsRegistration(registry).Apply(folder);

        Assert.Equal(Path.Combine(folder, "org.bringyourownagent.wallet.json"), registry.GetValue(HostKey, null));
        Assert.StartsWith($"\"{folder}", registry.GetValue(ProtocolKey + @"\shell\open\command", null));
    }

    [Fact]
    public void AFolderWithoutTheExtensionStillRegistersTheLinks()
    {
        var folder = InstalledFolder(withExtension: false);
        var registry = new FakeRegistry();

        new WindowsRegistration(registry).Apply(folder);

        Assert.Null(registry.GetValue(HostKey, null));
        Assert.NotNull(registry.GetValue(ProtocolKey + @"\shell\open\command", null));
    }

    [Fact]
    public void AnUninstallTakesBackOnlyWhatPointsIntoItsFolder()
    {
        var mine = InstalledFolder();
        var other = InstalledFolder();
        var registry = new FakeRegistry();
        var registration = new WindowsRegistration(registry);
        registration.Apply(mine);
        registration.SetStartWithWindows(mine, true);

        registration.Remove(other);                                           // another copy's uninstall leaves these alone
        Assert.NotNull(registry.GetValue(HostKey, null));
        Assert.NotNull(registry.GetValue(ProtocolKey + @"\shell\open\command", null));
        Assert.NotNull(registry.GetValue(RunKey, "Agent wallet"));

        registration.Remove(mine);
        Assert.Empty(registry.Values);
    }

    [Fact]
    public void StartWithWindowsStartsTheWalletInTheTrayAndCanBeSwitchedOff()
    {
        var folder = InstalledFolder();
        var registry = new FakeRegistry();
        var registration = new WindowsRegistration(registry);
        Assert.False(registration.StartsWithWindows(folder));

        registration.SetStartWithWindows(folder, true);
        Assert.True(registration.StartsWithWindows(folder));
        Assert.EndsWith("Byoa.Wallet.exe\" --background", registry.GetValue(RunKey, "Agent wallet"));

        registration.SetStartWithWindows(folder, false);
        Assert.False(registration.StartsWithWindows(folder));
        Assert.Null(registry.GetValue(RunKey, "Agent wallet"));
    }

    [Fact]
    public void TheVersionIsTheOneNumberTheWalletIsBuiltWith()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", WalletPaths.Version);
        Assert.Equal(typeof(WalletPaths).Assembly.GetName().Version!.ToString(3), WalletPaths.Version);
    }

    /// <summary>A test must never write to the real wallet's errors.log.</summary>
    static void NoLog(Exception error) { }

    sealed class FakeEngine : IUpdateEngine
    {
        public bool CanUpdateItself { get; set; } = true;
        public string? Newer;
        public Exception? CheckFails;
        public Exception? DownloadFails;
        public readonly List<string> Calls = [];

        public Task<string?> CheckAsync(CancellationToken ct)
        {
            Calls.Add("check");
            return CheckFails is null ? Task.FromResult(Newer) : Task.FromException<string?>(CheckFails);
        }

        public Task DownloadAsync(Action<int> progress, CancellationToken ct)
        {
            Calls.Add("download");
            progress(40);
            progress(100);
            return DownloadFails is null ? Task.CompletedTask : Task.FromException(DownloadFails);
        }

        public void ApplyAndRestart() => Calls.Add("apply");
    }

    [Fact]
    public async Task NothingIsAskedOfTheSiteUntilThePersonPressesTheButton()
    {
        var engine = new FakeEngine();
        var updater = new WalletUpdater(engine, () => { }, NoLog);
        Assert.Equal(UpdateState.Idle, updater.State);
        Assert.Empty(engine.Calls);

        await updater.CheckAsync();
        Assert.Equal(["check"], engine.Calls);
    }

    [Fact]
    public async Task TheNewestVersionIsSaid()
    {
        var updater = new WalletUpdater(new FakeEngine { Newer = null }, () => { }, NoLog);
        await updater.CheckAsync();
        Assert.Equal(UpdateState.UpToDate, updater.State);
        Assert.Null(updater.NewVersion);
    }

    [Fact]
    public async Task ANewVersionWaitsForTheWordUpdateAndThenTheWalletClosesAfterThePutInPlaceIsOrdered()
    {
        var engine = new FakeEngine { Newer = "0.31.0" };
        var quit = 0;
        var updater = new WalletUpdater(engine, () => { quit++; engine.Calls.Add("quit"); }, NoLog);
        var seen = new List<UpdateState>();
        updater.Changed += () => seen.Add(updater.State);

        await updater.CheckAsync();
        Assert.Equal(UpdateState.Available, updater.State);
        Assert.Equal("0.31.0", updater.NewVersion);
        Assert.Equal(["check"], engine.Calls);                                // found, but not downloaded: the person decides

        await updater.InstallAsync();
        Assert.Equal(["check", "download", "apply", "quit"], engine.Calls);   // the wallet closes only after the new version is ordered
        Assert.Equal(1, quit);
        Assert.Contains(UpdateState.Downloading, seen);
        Assert.Equal(100, updater.Percent);
    }

    [Fact]
    public async Task InstallDoesNothingBeforeAVersionWasFound()
    {
        var engine = new FakeEngine { Newer = "0.31.0" };
        var updater = new WalletUpdater(engine, () => Assert.Fail("must not close"), NoLog);
        await updater.InstallAsync();
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task AnUnreachableSiteIsSaidInPlainWordsAndTheWalletStaysOpen()
    {
        var logged = new List<Exception>();
        var updater = new WalletUpdater(new FakeEngine { CheckFails = new HttpRequestException("secret detail") }, () => Assert.Fail("must not close"), logged.Add);
        await updater.CheckAsync();
        Assert.Equal(UpdateState.Failed, updater.State);
        Assert.Single(logged);                                                 // the log (types and stacks only) gets it; the person gets plain words
        Assert.Contains("bringyourownagent.org", updater.Error);
        Assert.DoesNotContain("secret detail", updater.Error);
    }

    [Fact]
    public async Task AFailedDownloadKeepsTheWalletRunningAndAllowsAnotherTry()
    {
        var engine = new FakeEngine { Newer = "0.31.0", DownloadFails = new IOException("disk full") };
        var updater = new WalletUpdater(engine, () => Assert.Fail("must not close"), NoLog);
        await updater.CheckAsync();
        await updater.InstallAsync();

        Assert.Equal(UpdateState.Failed, updater.State);
        Assert.DoesNotContain("apply", engine.Calls);

        engine.DownloadFails = null;
        await updater.CheckAsync();                                           // the button after a failure is "Try again": it starts over
        Assert.Equal(UpdateState.Available, updater.State);
    }

    [Fact]
    public void TheWalletStartsAndSaysItCannotUpdateItselfWhenNothingSetVelopacksLocator()
    {
        // Main sets the locator (VelopackApp.Run); a test, or any start that skipped it, must still get a wallet, not an exception
        Assert.False(new VelopackEngine().CanUpdateItself);
        using var services = new WalletServices();
        Assert.False(services.Updater.CanUpdateItself);
    }

    [Fact]
    public void ACopyThatTheInstallerDidNotPutInPlaceSaysSo()
    {
        Assert.False(new NoUpdateEngine().CanUpdateItself);
        Assert.False(new WalletUpdater(new NoUpdateEngine(), () => { }, NoLog).CanUpdateItself);
    }
}
