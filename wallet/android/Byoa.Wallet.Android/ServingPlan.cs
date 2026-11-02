using Byoa.Wallet.Direct;

namespace Byoa.Wallet.Android;

/// <summary>What the foreground service has to do next.</summary>
enum ServingAction
{
    /// <summary>Nothing runs and nothing needs to.</summary>
    Nothing,

    /// <summary>Start the service (it is needed and it is not running).</summary>
    Start,

    /// <summary>The service runs and is needed: its notification is brought up to date.</summary>
    Update,

    /// <summary>The service runs and nothing needs it any more.</summary>
    Stop,

    /// <summary>The service failed to start again and again: it is not started once more, and the visitor is asked, in a notification, to open the wallet.</summary>
    GiveUp,
}

/// <summary>
/// The rules of the serving service, with nothing of Android in them (so that they are tested on any machine): the service is needed while a
/// site is connected, and while a site the visitor has just allowed is being connected (its models may be warming up for a minute, and the service
/// must be started while the visitor still looks at the app, because Android does not let an app in the background start one). What is
/// decided is always from the service's real state, never from what was asked of it before: a service that stopped by itself is started again.
/// </summary>
static class ServingPlan
{
    /// <summary>
    /// What to do next. <paramref name="serviceRunning"/>: the service exists, or was asked to start a moment ago. <paramref name="serviceForeground"/>: it has
    /// said what it is (called startForeground). A service that was asked to start and has not said it yet is never stopped from outside: Android then raises
    /// ForegroundServiceDidNotStartInTimeException in the app and ends it. Such a service stops itself as soon as it has started, if nothing needs it.
    /// </summary>
    public static ServingAction Next(int sites, int connecting, bool serviceRunning, bool serviceForeground = true) =>
        sites > 0 || connecting > 0
            ? serviceRunning ? ServingAction.Update : ServingAction.Start
            : serviceRunning && serviceForeground ? ServingAction.Stop : ServingAction.Nothing;

    /// <summary>How many times in a row a service may fail to start (it went away before it called startForeground) before it is not started again.</summary>
    public const int MaxFailedStarts = 3;

    /// <summary>
    /// What to do when the service says it is gone. A service that ended while it was needed is started again at once (the grace of a start that was asked
    /// is over with the service that it was asked for); one that went away before it called startForeground is started again a few times, and then the
    /// visitor is asked to open the wallet, so that a start that always fails does not go round in circles.
    /// </summary>
    public static ServingAction AfterGone(int sites, int connecting, bool wasForeground, int failedStarts)
    {
        if (sites == 0 && connecting == 0) return ServingAction.Nothing;
        return !wasForeground && failedStarts >= MaxFailedStarts ? ServingAction.GiveUp : ServingAction.Start;
    }

    /// <summary>
    /// The state of a site that has several connections, in one word: a connection that is serving counts before one that is connecting, that before one that
    /// waits to reconnect, and a connection that has only just closed counts last (it is still in the list for a moment after a newer one is up).
    /// </summary>
    public static DirectState BestState(IEnumerable<DirectState> states)
    {
        var best = DirectState.Closed;
        foreach (var state in states)
            if (Rank(state) < Rank(best)) best = state;
        return best;

        static int Rank(DirectState state) => state switch
        {
            DirectState.Connected => 0,
            DirectState.Connecting => 1,
            DirectState.Waiting => 2,
            _ => 3,
        };
    }

    /// <summary>The words of the service's notification: the sites served, or the site being connected.</summary>
    public static string Title(int sites, string? connectingHost) => sites switch
    {
        0 when connectingHost is not null => "Connecting to " + connectingHost + "…",
        0 => "Agent wallet is getting ready",
        1 => "Agent wallet is serving 1 site",
        _ => "Agent wallet is serving " + sites + " sites",
    };

    /// <summary>The line under the title.</summary>
    public static string Line(int sites) =>
        sites == 0 ? "The models are getting ready for the site you allowed." : "Sites you allowed are using your models. Tap to open the wallet.";
}
