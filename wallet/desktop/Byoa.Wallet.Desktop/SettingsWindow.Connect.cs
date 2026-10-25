using Avalonia.Controls;

namespace Byoa.Wallet.Desktop;

/// <summary>The Sites page's way in for a link that did not come through the browser on this computer.</summary>
sealed partial class SettingsWindow
{
    /// <summary>"Connect a site by link…": opens the window where the visitor pastes a link a site gave him on another device (CONTRACT-PAIR.md section 1).</summary>
    Control ConnectByLinkRow()
    {
        var button = Ui.Button("Connect a site by link…", "deny", "ConnectByLink", () => new ConnectByLinkWindow(_w).Show(this), "small");
        return Ui.Column(4, button, Ui.Wrap("A site open on your phone, or on a computer with no wallet, gives you a link. Paste it here.", "muted", "small"));
    }
}
