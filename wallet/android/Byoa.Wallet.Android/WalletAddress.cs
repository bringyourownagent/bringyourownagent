namespace Byoa.Wallet.Android;

/// <summary>What a piece of text from a QR code, or an address that opened the app, turned out to be.</summary>
enum CodeKind
{
    /// <summary>A <c>bringyourownagent://connect?...</c> link: it goes to the wallet as a handed link (it came from another device's screen).</summary>
    Link,

    /// <summary>A site's hand-off address (<c>https://&lt;site&gt;/byoa/hand/...</c>): the browser opens it; the wallet does not act on it.</summary>
    HandOff,

    /// <summary>Nothing this wallet takes.</summary>
    Nothing,
}

/// <summary>The result of looking at a text: its kind, the text to act on (a link, or an address for the browser) and, for Nothing, the plain line to tell the visitor.</summary>
readonly record struct CodeReading(CodeKind Kind, string? Value, string? Line)
{
    public static CodeReading Link(string link) => new(CodeKind.Link, link, null);

    public static CodeReading HandOff(string address) => new(CodeKind.HandOff, address, null);

    public static CodeReading Nothing(string line) => new(CodeKind.Nothing, null, line);
}

/// <summary>
/// The addresses and codes the wallet takes from another device's screen, with nothing of Android in them (so they are tested on any machine).
/// A site's QR code carries <c>https://wallet.bringyourownagent.org/#link=&lt;the bringyourownagent link, as it is&gt;</c> (the address of the web
/// wallet, because a phone camera does not open a custom scheme); the Android wallet is registered for that address, takes the link out of the fragment and
/// hands it on as a handed link (CONTRACT-PAIR section 1). Only that exact shape is taken: the host, https, the default port, no user name, the path "/", a
/// fragment that starts with "link=" and holds a <c>bringyourownagent://connect?</c> link. Nothing here touches the network; whatever else an address
/// holds is never looked at.
/// </summary>
static class WalletAddress
{
    /// <summary>The web wallet's host: the address that a site's QR code carries, and the one the app is registered for.</summary>
    public const string WebWalletHost = "wallet.bringyourownagent.org";

    const string LinkPrefix = "bringyourownagent://connect?";
    const string FragmentKey = "link=";
    const string EncodedScheme = "bringyourownagent%3a";

    /// <summary>The longest address looked at: a link is at most 12 KiB, and the address wraps it.</summary>
    const int MaxAddress = 32 * 1024;

    /// <summary>The one plain line when an address that opened the app carried no link from a site.</summary>
    public const string NoLinkLine = "That address did not carry a link from a site, so nothing was connected. Ask the site for its code again.";

    /// <summary>The plain line when the camera read a code that is not a site's.</summary>
    public const string NotASiteLine = "That code is not from a site that uses this wallet.";

    /// <summary>
    /// The link in an address that opened the app: <c>https://wallet.bringyourownagent.org/#link=bringyourownagent://connect?...</c> (the link as it is, or
    /// written as one percent-encoded value, as the web wallet also takes it). False when the address is anything else.
    /// </summary>
    public static bool TryLinkFromAddress(string? address, out string link)
    {
        link = "";
        if (string.IsNullOrEmpty(address) || address.Length > MaxAddress) return false;
        foreach (var c in address) if (c <= ' ' || c == '\u007f') return false;           // an address has no space, no line break, no control character
        var hash = address.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0) return false;
        if (!Uri.TryCreate(address[..hash], UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, WebWalletHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/") return false;

        var fragment = address[(hash + 1)..];
        if (!fragment.StartsWith(FragmentKey, StringComparison.Ordinal)) return false;
        var value = fragment[FragmentKey.Length..];
        if (value.StartsWith(EncodedScheme, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                value = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        if (!value.StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        link = value;
        return true;
    }

    /// <summary>
    /// What the camera read. A <c>bringyourownagent://connect?</c> link and the web wallet's address are links (handed ones); a site's hand-off address
    /// (<c>https://&lt;site&gt;/byoa/hand/...</c>) is for the browser, which is the site's page, not the wallet's business; anything else is nothing.
    /// </summary>
    public static CodeReading Read(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > MaxAddress) return CodeReading.Nothing(NotASiteLine);
        if (t.StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase)) return CodeReading.Link(t);
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return CodeReading.Nothing(NotASiteLine);
        if (string.Equals(uri.Host, WebWalletHost, StringComparison.OrdinalIgnoreCase))
            return TryLinkFromAddress(t, out var link) ? CodeReading.Link(link) : CodeReading.Nothing(NoLinkLine);
        // the path of a site's hand-off page (CONTRACT-PAIR section 2), a token after it; the page is the site's and the visitor's browser opens it
        if (uri.AbsolutePath.StartsWith("/byoa/hand/", StringComparison.Ordinal) && uri.AbsolutePath.Length > "/byoa/hand/".Length) return CodeReading.HandOff(t);
        return CodeReading.Nothing(NotASiteLine);
    }
}
