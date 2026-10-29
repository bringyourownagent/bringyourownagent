using Byoa.Wallet.Protocol;
using Velopack;
using Velopack.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>What "Check for updates" does underneath; Velopack in the wallet, a stand-in in tests.</summary>
interface IUpdateEngine
{
    /// <summary>True when this copy was put in place by the installer (Setup.exe), so it can replace itself.</summary>
    bool CanUpdateItself { get; }

    /// <summary>The newer version on the site, or null when this one is the newest.</summary>
    Task<string?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads what <see cref="CheckAsync"/> found; the argument gets the percent done.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken ct);

    /// <summary>Ends this program, puts the downloaded version in place, and starts it again.</summary>
    void ApplyAndRestart();
}

enum UpdateState
{
    /// <summary>Nothing asked yet.</summary>
    Idle,
    Checking,
    UpToDate,

    /// <summary>A newer version is on the site; the person has not yet pressed Update.</summary>
    Available,
    Downloading,
    Failed,
}

/// <summary>
/// The wallet's "Check for updates": the person presses it, the wallet looks at the site's feed, tells what it found, and on
/// "Update and restart" downloads the new version, closes, and comes back as the new version. Nothing is asked of the site until the
/// person presses the button: the wallet makes no call of its own to check.
/// </summary>
sealed class WalletUpdater(IUpdateEngine engine, Action quit, Action<Exception>? log = null)
{
    CancellationTokenSource? _running;

    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>The newer version while <see cref="State"/> is Available or Downloading.</summary>
    public string? NewVersion { get; private set; }

    /// <summary>Percent downloaded, while <see cref="State"/> is Downloading.</summary>
    public int Percent { get; private set; }

    /// <summary>What went wrong, in words for the person, while <see cref="State"/> is Failed.</summary>
    public string? Error { get; private set; }

    public bool CanUpdateItself => engine.CanUpdateItself;

    /// <summary>Raised on every change of the above, from any thread.</summary>
    public event Action? Changed;

    public async Task CheckAsync()
    {
        if (State is UpdateState.Checking or UpdateState.Downloading) return;
        _running = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Set(UpdateState.Checking, newVersion: null);
        try
        {
            var found = await engine.CheckAsync(_running.Token).ConfigureAwait(false);
            Set(found is null ? UpdateState.UpToDate : UpdateState.Available, found);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e);
        }
    }

    /// <summary>Downloads the version <see cref="CheckAsync"/> found and restarts into it.</summary>
    public async Task InstallAsync()
    {
        if (State != UpdateState.Available) return;
        _running = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        Percent = 0;
        Set(UpdateState.Downloading, NewVersion);
        try
        {
            await engine.DownloadAsync(percent =>
            {
                Percent = percent;
                Changed?.Invoke();
            }, _running.Token).ConfigureAwait(false);
            engine.ApplyAndRestart();
            quit();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e);
        }
    }

    void Set(UpdateState state, string? newVersion)
    {
        State = state;
        NewVersion = newVersion;
        Error = null;
        Changed?.Invoke();
    }

    void Fail(Exception e)
    {
        (log ?? (error => ErrorLog.Write(WalletPaths.Home, error)))(e);
        State = UpdateState.Failed;
        Error = e is OperationCanceledException ? "The site did not answer in time. Try again later."
            : e is HttpRequestException ? "Could not reach bringyourownagent.org. Check the internet connection and try again."
            : "The update did not work: " + e.Message;
        Changed?.Invoke();
    }
}

/// <summary>Velopack looking at the wallet's own feed on the site.</summary>
sealed class VelopackEngine : IUpdateEngine
{
    /// <summary>Where the releases are: <c>releases.win.json</c> and the packages it lists.</summary>
    public const string FeedUrl = "https://bringyourownagent.org/downloads/win";

    /// <summary>A test run (a wallet with its own data folder) may name another feed, a folder or an address on this computer.</summary>
    public const string FeedVariable = "BYOA_UPDATE_FEED";

    readonly string _feed = FeedUrl;
    UpdateManager? _manager;
    UpdateInfo? _found;

    public VelopackEngine()
    {
        if (WalletPaths.IsCustomHome && Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } given) _feed = given;
    }

    // made at first use: it needs the locator that VelopackApp.Run() sets at the top of Main, and the wallet must start without it too
    UpdateManager Manager => _manager ??= new UpdateManager(_feed);

    public bool CanUpdateItself
    {
        get
        {
            try { return Manager.IsInstalled; }
            catch (InvalidOperationException) { return false; }     // no locator: not started through Main (a test)
        }
    }

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        _found = await Manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        return _found?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken ct)
    {
        if (_found is null) throw new InvalidOperationException("Check for updates first.");
        return Manager.DownloadUpdatesAsync(_found, progress, ct);
    }

    public void ApplyAndRestart()
    {
        if (_found is null) throw new InvalidOperationException("Check for updates first.");
        // Update.exe waits for this process to end, so the wallet closes itself the usual way (see WalletUpdater); no restart arguments:
        // the new version opens its window, so the person sees that it came back.
        Manager.WaitExitThenApplyUpdates(_found.TargetFullRelease, silent: false, restart: true);
    }
}

/// <summary>For a copy that cannot update itself (a build from the source, the macOS app): the button opens the download page.</summary>
sealed class NoUpdateEngine : IUpdateEngine
{
    public bool CanUpdateItself => false;

    public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task DownloadAsync(Action<int> progress, CancellationToken ct) => Task.CompletedTask;

    public void ApplyAndRestart() { }
}
