using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>The foot of the tab list: the version, "Check for updates", and "Start with Windows" or "Start at login".</summary>
sealed partial class SettingsWindow
{
    /// <summary>Where a copy that cannot update itself sends the person.</summary>
    const string DownloadPage = "https://bringyourownagent.org/wallet";

    readonly StackPanel _updateBox = new() { Spacing = 4 };

    /// <summary>Starts to show the state of the updater, and keeps showing it until the window closes.</summary>
    void WatchUpdates()
    {
        void OnChanged() => Dispatcher.UIThread.Post(RenderUpdates);
        _w.Updater.Changed += OnChanged;
        Closed += (_, _) => _w.Updater.Changed -= OnChanged;
        RenderUpdates();
    }

    // The notice «This is the newest version.» comes with the answer of a check (not with a redraw) and fades away over NoticeFade.
    static readonly TimeSpan NoticeFade = TimeSpan.FromSeconds(7);
    DateTime? _newestSince;
    UpdateState? _lastState;

    void RenderUpdates()
    {
        _updateBox.Children.Clear();
        var updater = _w.Updater;
        if (updater.State == UpdateState.UpToDate && _lastState != UpdateState.UpToDate) _newestSince = DateTime.UtcNow;
        else if (updater.State != UpdateState.UpToDate) _newestSince = null;
        _lastState = updater.State;

        var version = Ui.Text("Version " + WalletPaths.Version, "muted", "small");
        if (!updater.CanUpdateItself)
        {
            // a build from the source, or the zip of an earlier release: it has no installer to replace it with a newer one
            _updateBox.Children.Add(version);
            _updateBox.Children.Add(Ui.Button("Get the latest version", "deny", "UpdateDownloadPage", OpenDownloadPage, "small"));
            return;
        }

        // the version, and a small refresh sign behind it that checks for an update (no big button: the foot of the list keeps its room)
        var busy = updater.State is UpdateState.Checking or UpdateState.Downloading;
        var refresh = new Button { Content = Ui.Glyph(Ui.Refresh, Ui.Brush("Ink2"), 14, 2.2), IsEnabled = !busy };
        refresh.Classes.Add("iconbtn");
        ToolTip.SetTip(refresh, "Check for updates");
        AutomationProperties.SetAutomationId(refresh, "UpdateCheck");
        AutomationProperties.SetName(refresh, "Check for updates");
        refresh.Click += (_, _) => Check();
        _updateBox.Children.Add(Ui.Row(4, version, refresh));

        switch (updater.State)
        {
            case UpdateState.Checking:
                _updateBox.Children.Add(Ui.Text("Looking for a new version…", "muted", "small"));
                break;
            case UpdateState.UpToDate:
                AddNewestNotice();
                break;
            case UpdateState.Available:
                _updateBox.Children.Add(Ui.Wrap($"Version {updater.NewVersion} is ready.", "small"));
                _updateBox.Children.Add(Ui.Button("Update and restart", "allow", "UpdateInstall", Install, "small"));
                _updateBox.Children.Add(Ui.Wrap("The wallet closes for a few seconds and opens again. Your settings stay.", "muted", "small"));
                break;
            case UpdateState.Downloading:
                _updateBox.Children.Add(Ui.Text($"Downloading {updater.NewVersion}… {updater.Percent}%", "muted", "small"));
                _updateBox.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = updater.Percent, Classes = { "meter" } });
                break;
            case UpdateState.Failed:
                _updateBox.Children.Add(Ui.Wrap(updater.Error ?? "The update did not work.", "muted", "small"));
                break;
        }

        if (_w.Registration is { } registration)
        {
            var folder = AppContext.BaseDirectory;
            var startup = new CheckBox { Content = "Start with Windows", IsChecked = registration.StartsWithWindows(folder), Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            AutomationProperties.SetAutomationId(startup, "StartWithWindows");
            startup.IsCheckedChanged += (_, _) => registration.SetStartWithWindows(folder, startup.IsChecked == true);
            _updateBox.Children.Add(startup);
        }
        else if (_w.LinuxRegistration is { } linux)
        {
            var folder = AppContext.BaseDirectory;
            var startup = new CheckBox { Content = "Start at login", IsChecked = linux.StartsAtLogin(folder), Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            AutomationProperties.SetAutomationId(startup, "StartAtLogin");
            startup.IsCheckedChanged += (_, _) => linux.SetStartAtLogin(folder, startup.IsChecked == true);
            _updateBox.Children.Add(startup);
        }
    }

    /// <summary>"This is the newest version." in full strength at first, then fading to nothing over seven seconds, and gone.</summary>
    void AddNewestNotice()
    {
        if (_newestSince is not { } since) return;
        var left = NoticeFade - (DateTime.UtcNow - since);
        if (left <= TimeSpan.Zero) return;
        var note = Ui.Wrap("This is the newest version.", "muted", "small");
        note.Opacity = Math.Clamp(left / NoticeFade, 0, 1);
        note.Transitions = new Transitions { new DoubleTransition { Property = Avalonia.Visual.OpacityProperty, Duration = left } };
        _updateBox.Children.Add(note);
        Dispatcher.UIThread.Post(() => note.Opacity = 0, DispatcherPriority.Background);       // the fade starts once the notice is on the screen
        DispatcherTimer.RunOnce(RenderUpdates, left + TimeSpan.FromMilliseconds(150));         // and then the notice is taken away
    }

    void Check() => _ = _w.Updater.CheckAsync();

    void Install() => _ = _w.Updater.InstallAsync();

    void OpenDownloadPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DownloadPage) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _w.Ui.ShowInfo(new InfoModel("Updates", "Could not open the browser. Go to " + DownloadPage + " yourself."));
        }
    }
}
