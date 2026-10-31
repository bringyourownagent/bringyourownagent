using Avalonia.Automation;
using Avalonia.Controls;

namespace Byoa.Wallet.Desktop;

/// <summary>What the consent window adds for a link that did not come through the browser on this computer (CONTRACT-PAIR.md section 1).</summary>
sealed partial class ConsentWindow
{
    /// <summary>For a site that shows no check code, when the link also came from outside the browser.</summary>
    const string HandedNoCheck = "This link came from outside the browser and the site gives no way to check it. Allow only if you started it yourself, just now.";

    /// <summary>Where the link came from, and what to do about it: a link that went through a messenger is how a stranger would send one.</summary>
    Control HandedNotice()
    {
        var note = Ui.Wrap("This link did not come through the browser on this computer. It came from another device or was pasted by hand: check the code.", "strong", "small");
        AutomationProperties.SetAutomationId(note, "ConsentHanded");
        return Ui.Box("field", note);
    }
}
