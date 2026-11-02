using Android.App;
using Android.Content;
using Byoa.Wallet.Direct;

namespace Byoa.Wallet.Android;

/// <summary>
/// Keeps <see cref="ServingService"/> running exactly while it is needed: while a site is connected (a live connection of the direct transport), and from the
/// moment the visitor allows a site until its connection is made or has failed (the models may be getting ready for a while, and the service has to be
/// started while the app is on the screen, since Android refuses it to an app in the background). What it does is decided by <see cref="ServingPlan"/> from
/// the service's real state. "Stop" in the notification ends the connections like quitting the desktop wallet (the sites keep what they were allowed;
/// none is paused).
/// </summary>
sealed class ServingController : IDisposable
{
    /// <summary>How long after a start was asked the service counts as running (it takes a moment to get going).</summary>
    static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(4);

    /// <summary>The scope of the link that is being acted on: the consent, answered Allow in it, says here which site is being connected.</summary>
    internal static readonly AsyncLocal<Connect?> Link = new();

    readonly Context _context;
    readonly DirectTransport _direct;
    readonly object _gate = new();
    readonly List<Connect> _connecting = [];

    /// <summary>The connections that "Stop" ended: they take a moment to close, and in that moment they do not count as sites served.</summary>
    readonly HashSet<DirectConnection> _stopped = [];
    DateTime _startAskedAt = DateTime.MinValue;

    /// <summary>How many times in a row the service went away before it called startForeground.</summary>
    int _failedStarts;

    public ServingController(Context context, DirectTransport direct)
    {
        _context = context;
        _direct = direct;
        _direct.Changed += OnChanged;
    }

    /// <summary>
    /// A link that is being acted on. After Allow its site is being connected (the models may be getting ready for a minute); "Stop" cancels it, and the
    /// link's preparation honours that (the engine stops waiting for the models and hands out no grant), so that the site never gets a hello.
    /// </summary>
    internal sealed class Connect : IDisposable
    {
        readonly CancellationTokenSource _cts = new();

        public string? Host { get; set; }

        /// <summary>Handed to the direct transport with the link.</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>The visitor pressed Stop while this link was being connected.</summary>
        public bool Stopped => _cts.IsCancellationRequested;

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cts.Dispose();
    }

    /// <summary>Raised when the connections change (the home screen shows them).</summary>
    public event Action? Changed;

    /// <summary>The sites that have a connection that is not over: connecting, serving, or waiting to reconnect.</summary>
    public IReadOnlyList<string> Origins
    {
        get
        {
            lock (_gate) return _direct.Connections.Where(c => c.State != DirectState.Closed && !_stopped.Contains(c)).Select(c => c.Origin).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    public int Sites => Origins.Count;

    /// <summary>A site is allowed and its connection is being made.</summary>
    public bool IsConnecting
    {
        get
        {
            lock (_gate) return _connecting.Any(c => c.Host is not null && !c.Stopped);
        }
    }

    internal string? ConnectingHostNow => ConnectingHost;

    string? ConnectingHost
    {
        get
        {
            lock (_gate) return _connecting.Where(c => !c.Stopped).Select(c => c.Host).FirstOrDefault(h => h is not null);
        }
    }

    /// <summary>Something needs the service: a site is connected or being connected.</summary>
    public bool Needed => Sites > 0 || IsConnecting;

    /// <summary>
    /// The visitor has just allowed <paramref name="host"/> (the consent was answered Allow, and the app is still on the screen): the service starts now
    /// and says that it is connecting, so that the models getting ready cannot push its start into the background.
    /// </summary>
    public void Connecting(string host)
    {
        if (Link.Value is { } scope) scope.Host = host;
        else return;
        lock (_gate)
        {
            if (!_connecting.Contains(scope)) _connecting.Add(scope);
        }

        Sync();
        Changed?.Invoke();
    }

    /// <summary>The link has been acted on to its end: a connection exists, or the site was not connected after all.</summary>
    public void ConnectingDone(Connect scope)
    {
        lock (_gate) _connecting.Remove(scope);
        Sync();
        Changed?.Invoke();
        scope.Dispose();
    }

    /// <summary>The service has called startForeground: starting it works.</summary>
    internal void ServiceStarted()
    {
        lock (_gate) _failedStarts = 0;
    }

    /// <summary>
    /// The service is gone (it was stopped, it ended by itself, or it failed before it called startForeground). Whatever was asked of it is over with it: the
    /// grace of a start is cleared, so that what is still needed is started again at once, and not after the grace; a start that fails again and again
    /// is given up after a few tries, and the visitor is asked to open the wallet.
    /// </summary>
    internal void ServiceGone(bool wasForeground)
    {
        int sites = Sites;
        int connecting;
        int failed;
        lock (_gate)
        {
            _startAskedAt = DateTime.MinValue;
            connecting = _connecting.Count(c => c.Host is not null && !c.Stopped);
            failed = wasForeground ? _failedStarts = 0 : ++_failedStarts;
        }

        switch (ServingPlan.AfterGone(sites, connecting, wasForeground, failed))
        {
            case ServingAction.Start:
                Sync();
                break;
            case ServingAction.GiveUp:
                ErrorLog.Write(new InvalidOperationException("The serving service did not start " + failed + " times in a row."));
                try
                {
                    Notifier.PostOpenToServe(_context);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    ErrorLog.Write(e);
                }

                break;
        }
    }

    void OnChanged()
    {
        Sync();
        Changed?.Invoke();
    }

    /// <summary>
    /// The state of things may have changed (the service stopped by itself, the app came to the screen, the visitor answered the system's question about
    /// notifications): what is needed is done again, from the service's real state.
    /// </summary>
    public void Refresh() => Sync();

    void Sync()
    {
        int sites = Sites;
        string? host;
        int connecting;
        bool running;
        bool foreground;
        lock (_gate)
        {
            connecting = _connecting.Count(c => c.Host is not null && !c.Stopped);
            host = _connecting.Where(c => !c.Stopped).Select(c => c.Host).FirstOrDefault(h => h is not null);
            running = ServingService.Running || DateTime.UtcNow - _startAskedAt < StartGrace;
            foreground = ServingService.Foreground;
        }

        try
        {
            switch (ServingPlan.Next(sites, connecting, running, foreground))
            {
                case ServingAction.Start:
                    Notifier.EnsureChannels(_context);
                    _context.StartForegroundService(new Intent(_context, typeof(ServingService)));
                    lock (_gate) _startAskedAt = DateTime.UtcNow;
                    Notifier.CancelOpenToServe(_context);
                    break;
                case ServingAction.Update:
                    var manager = (NotificationManager)_context.GetSystemService(Context.NotificationService)!;
                    manager.Notify(Notifier.ServingId, Notifier.Serving(_context, sites, host));
                    break;
                case ServingAction.Stop:
                    _context.StopService(new Intent(_context, typeof(ServingService)));
                    lock (_gate) _startAskedAt = DateTime.MinValue;
                    break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Android refused to start a service from the background: the wallet goes on without it, as long as the system lets the app live, and the
            // visitor is asked, in a notification, to open the app (which starts it); Refresh tries again when the app comes to the screen
            ErrorLog.Write(e);
            if (ServingPlan.Next(sites, connecting, running) == ServingAction.Start) Notifier.PostOpenToServe(_context);
        }
    }

    /// <summary>
    /// The notification's "Stop" (and Home's): every connection ends now, and a link that is still being connected is cancelled, as when the desktop wallet is quit, and the service stops with the last one. No site is
    /// paused and nothing it was allowed changes: a site that has a stored pairing comes back when the wallet starts serving again (the app is opened,
    /// or a new link arrives).
    /// </summary>
    public void StopAll()
    {
        List<Connect> pending;
        lock (_gate) pending = _connecting.ToList();

        // A link that is still being connected (its models getting ready) has no connection yet: it is cancelled first, and the transport refuses to add a
        // connection for a link whose token is cancelled. The connections are listed after that, so that one that was added before the cancellation is in the list.
        foreach (var scope in pending) scope.Cancel();
        var all = _direct.Connections.ToList();
        lock (_gate)
        {
            _stopped.RemoveWhere(c => c.State == DirectState.Closed);
            foreach (var connection in all) _stopped.Add(connection);
        }

        foreach (var connection in all) connection.Stop("stopped from the notification");
        Sync();
        Changed?.Invoke();
    }

    public void Dispose() => _direct.Changed -= OnChanged;
}
