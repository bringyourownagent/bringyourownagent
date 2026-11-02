using Byoa.Wallet.Android;
using Byoa.Wallet.Direct;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The rules of the Android wallet's foreground service (<see cref="ServingPlan"/>, whose file is compiled into these tests): the service is needed while a
/// site is connected and from the moment a site is allowed until it is connected, it is started when it is needed and is not running, and what it does
/// is decided from whether it really runs, never from what was once asked of it.
/// </summary>
public class ServingPlanTests
{
    [Fact]
    public void ANeededServiceThatIsNotRunningIsStarted()
    {
        Assert.Equal(ServingAction.Start, ServingPlan.Next(sites: 1, connecting: 0, serviceRunning: false));
        Assert.Equal(ServingAction.Start, ServingPlan.Next(sites: 0, connecting: 1, serviceRunning: false));
        Assert.Equal(ServingAction.Start, ServingPlan.Next(sites: 3, connecting: 2, serviceRunning: false));
    }

    [Fact]
    public void ANeededServiceThatRunsHasItsNotificationUpdated()
    {
        Assert.Equal(ServingAction.Update, ServingPlan.Next(1, 0, true));
        Assert.Equal(ServingAction.Update, ServingPlan.Next(0, 1, true));
    }

    [Fact]
    public void AServiceNothingNeedsIsStoppedOnlyWhenItRuns()
    {
        Assert.Equal(ServingAction.Stop, ServingPlan.Next(0, 0, true));
        Assert.Equal(ServingAction.Nothing, ServingPlan.Next(0, 0, false));
    }

    [Fact]
    public void AServiceThatStoppedByItselfIsStartedAgainWhileASiteIsConnected()
    {
        // the review's case: the service ended (it saw no site, or its start failed) while "running" was still believed; going by the real state, the next
        // look finds it not running and starts it, however many times it was started before
        var running = true;
        Assert.Equal(ServingAction.Update, ServingPlan.Next(1, 0, running));
        running = false;                                          // it stopped by itself
        Assert.Equal(ServingAction.Start, ServingPlan.Next(1, 0, running));
        running = true;
        Assert.Equal(ServingAction.Update, ServingPlan.Next(1, 0, running));
    }

    [Fact]
    public void TheServiceStaysWhileASiteIsBeingConnectedAndGoesWhenTheConnectionIsNeitherMadeNorComing()
    {
        // Allow: connecting, no connection yet
        Assert.Equal(ServingAction.Start, ServingPlan.Next(0, 1, false));
        Assert.Equal(ServingAction.Update, ServingPlan.Next(0, 1, true));
        // the connection is up, the link is done
        Assert.Equal(ServingAction.Update, ServingPlan.Next(1, 0, true));
        // the connection was not made (the models failed to get ready): nothing needs the service
        Assert.Equal(ServingAction.Stop, ServingPlan.Next(0, 0, true));
    }

    [Fact]
    public void AServiceThatHasNotSaidWhatItIsYetIsNeverStoppedFromOutside()
    {
        // the crash the phone showed: Stop in the notification, the last connection closes a moment later, the service had just been started again by the
        // "ended by itself" rule and had not called startForeground; stopping it then makes Android end the app (ForegroundServiceDidNotStartInTime)
        Assert.Equal(ServingAction.Nothing, ServingPlan.Next(0, 0, serviceRunning: true, serviceForeground: false));
        // once it has said it, it is stopped as before
        Assert.Equal(ServingAction.Stop, ServingPlan.Next(0, 0, serviceRunning: true, serviceForeground: true));
        // and a service that is needed is not touched by this rule
        Assert.Equal(ServingAction.Update, ServingPlan.Next(1, 0, serviceRunning: true, serviceForeground: false));
        Assert.Equal(ServingAction.Start, ServingPlan.Next(1, 0, serviceRunning: false, serviceForeground: false));
    }

    [Fact]
    public void AServiceThatEndedWhileItWasNeededIsStartedAgainAtOnce()
    {
        // it had called startForeground and went away by itself: it is started again, and the failures so far do not count
        Assert.Equal(ServingAction.Start, ServingPlan.AfterGone(sites: 1, connecting: 0, wasForeground: true, failedStarts: 0));
        Assert.Equal(ServingAction.Start, ServingPlan.AfterGone(sites: 0, connecting: 1, wasForeground: true, failedStarts: 0));
        // nothing needs it: nothing is done
        Assert.Equal(ServingAction.Nothing, ServingPlan.AfterGone(0, 0, wasForeground: true, failedStarts: 0));
        Assert.Equal(ServingAction.Nothing, ServingPlan.AfterGone(0, 0, wasForeground: false, failedStarts: 5));
    }

    [Fact]
    public void AServiceThatFailedBeforeStartForegroundIsStartedAgainAFewTimesAndThenTheVisitorIsAsked()
    {
        // the review's case: startForeground threw, the service stopped itself, and the old grace made the controller think it was running
        for (var failed = 1; failed < ServingPlan.MaxFailedStarts; failed++)
            Assert.Equal(ServingAction.Start, ServingPlan.AfterGone(1, 0, wasForeground: false, failedStarts: failed));
        Assert.Equal(ServingAction.GiveUp, ServingPlan.AfterGone(1, 0, wasForeground: false, failedStarts: ServingPlan.MaxFailedStarts));
        Assert.Equal(ServingAction.GiveUp, ServingPlan.AfterGone(0, 2, wasForeground: false, failedStarts: ServingPlan.MaxFailedStarts + 4));
    }

    [Fact]
    public void ASiteThatHasSeveralConnectionsIsToldByTheBestOfThem()
    {
        // the review's case: a connection that has only just closed is still in the list beside the newer one that is up
        Assert.Equal(DirectState.Connected, ServingPlan.BestState([DirectState.Closed, DirectState.Connected]));
        Assert.Equal(DirectState.Connected, ServingPlan.BestState([DirectState.Waiting, DirectState.Connected, DirectState.Closed]));
        Assert.Equal(DirectState.Connecting, ServingPlan.BestState([DirectState.Waiting, DirectState.Connecting]));
        Assert.Equal(DirectState.Waiting, ServingPlan.BestState([DirectState.Closed, DirectState.Waiting]));
        Assert.Equal(DirectState.Closed, ServingPlan.BestState([DirectState.Closed]));
        Assert.Equal(DirectState.Closed, ServingPlan.BestState([]));
    }

    [Fact]
    public void TheNotificationSaysWhatIsHappening()
    {
        Assert.Equal("Agent wallet is serving 1 site", ServingPlan.Title(1, null));
        Assert.Equal("Agent wallet is serving 2 sites", ServingPlan.Title(2, null));
        Assert.Equal("Connecting to game.example…", ServingPlan.Title(0, "game.example"));
        Assert.Equal("Agent wallet is serving 1 site", ServingPlan.Title(1, "other.example"));   // a connected site is what is said, whatever else is coming
        Assert.Equal("Agent wallet is getting ready", ServingPlan.Title(0, null));
        Assert.Contains("getting ready", ServingPlan.Line(0), StringComparison.Ordinal);
        Assert.Contains("Tap to open", ServingPlan.Line(1), StringComparison.Ordinal);
    }
}
