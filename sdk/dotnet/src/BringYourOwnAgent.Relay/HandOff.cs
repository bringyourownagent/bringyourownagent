using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BringYourOwnAgent.Relay;

/// <summary>A freshly issued code: what <see cref="DirectHub.Issue"/> hands to the endpoint that answers the page.</summary>
/// <param name="Code">The one-time session code (the link's <c>session</c>).</param>
/// <param name="Link">The <c>bringyourownagent://connect</c> link.</param>
/// <param name="Check">The four-character check code the page shows (CONTRACT-DIRECT.md §3a).</param>
/// <param name="HandOff">The hand-off address, <c>https://host/byoa/hand/&lt;token&gt;</c>, when the site offers one (CONTRACT-PAIR.md §2); else null.</param>
/// <param name="Short">The typed form of the hand-off address, <c>K7MQ-3XPZ</c>, entered at <c>https://host/byoa/hand</c>; null with <paramref name="HandOff"/>.</param>
public sealed record IssuedCode(string Code, string Link, string Check, string? HandOff, string? Short);

/// <summary>What <see cref="DirectHub.HandOffOpen"/> found.</summary>
public enum HandOffOutcome
{
    /// <summary>The link is released; this was its one use.</summary>
    Released,
    /// <summary>A token that was spent already ("this link was already used").</summary>
    Used,
    /// <summary>A token the server does not know, or one that has died (its 120 seconds, or its code).</summary>
    Gone,
    /// <summary>A short code that is wrong, spent or expired: the three are told alike.</summary>
    Wrong,
    /// <summary>Too many wrong short codes from this address, or from all addresses together: no short code is looked at.</summary>
    Throttled,
}

/// <summary>The answer to a click on the hand-off page.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Link">The link, for <see cref="HandOffOutcome.Released"/> only.</param>
public readonly record struct HandOffResult(HandOffOutcome Outcome, string? Link);

/// <summary>
/// The words of the hand-off page (CONTRACT-PAIR.md §2), in English. A site gives its own (<see cref="RelayOptions.HandOffLabels"/>, or a
/// function that picks them per request). They are set into the page as text, never as markup.
/// </summary>
public sealed class HandOffLabels
{
    /// <summary>The page's <c>lang</c> attribute.</summary>
    public string Lang { get; set; } = "en";
    /// <summary>The title and the heading of the page.</summary>
    public string Title { get; set; } = "Open your agent wallet";
    /// <summary>The one explanation on the page.</summary>
    public string Explain { get; set; } = "A site asked to connect to your agent wallet. Press the button: your wallet opens and asks you before it connects. Allow only if the check code it shows is the same as on the page where you started.";
    /// <summary>The main button.</summary>
    public string Open { get; set; } = "Open my wallet";
    /// <summary>The second, smaller button on a computer: the web wallet.</summary>
    public string OpenWeb { get; set; } = "Use the web wallet instead";
    /// <summary>The heading above the field where a short code is typed (the page without a token).</summary>
    public string ShortTitle { get; set; } = "Type the code from the other screen";
    /// <summary>The label of the field where a short code is typed.</summary>
    public string ShortLabel { get; set; } = "Code";
    /// <summary>The hint under the short-code field.</summary>
    public string ShortHint { get; set; } = "Eight letters and digits, like K7MQ-3XPZ.";
    /// <summary>Shown while the page asks the site for the link.</summary>
    public string Working { get; set; } = "One moment…";
    /// <summary>Shown after the page has opened the wallet. It does not say whether the wallet started: the page cannot know.</summary>
    public string Opened { get; set; } = "Your wallet asks you before it connects. If nothing opened, you can use the web wallet.";
    /// <summary>A token that was spent already.</summary>
    public string ErrorUsed { get; set; } = "This link was already used. Start again on the site.";
    /// <summary>A token that is unknown or has expired.</summary>
    public string ErrorGone { get; set; } = "This link has expired. Start again on the site.";
    /// <summary>A short code that did not work, whatever the reason.</summary>
    public string ErrorWrong { get; set; } = "That code did not work. Check it, or start again on the site.";
    /// <summary>Too many tries.</summary>
    public string ErrorSlow { get; set; } = "Too many tries. Wait a few minutes and try again.";
    /// <summary>The site could not be reached.</summary>
    public string ErrorNetwork { get; set; } = "Could not reach the site. Try again.";
    /// <summary>For a browser that runs no script.</summary>
    public string NoScript { get; set; } = "This page needs JavaScript to open your wallet.";
}

/// <summary>
/// The hand-off page of CONTRACT-PAIR.md §2 and what it is served with. The page is small and plain: one script and one style, both in
/// the page itself and allowed by hash in a strict Content-Security-Policy (no external script, style, font or image). A <c>GET</c> shows
/// the same page for every address, holds no link and no code, and reflects nothing of the request; the link comes only from a <c>POST</c>
/// the page makes from a click.
/// </summary>
public static class HandOffPage
{
    static readonly string Script = Resource("HandOffPage.js");
    static readonly string Style = Resource("HandOffPage.css");

    /// <summary>The Content-Security-Policy of the page: only its own script and style (by hash), the network only to this origin, no framing.</summary>
    public static readonly string ContentSecurityPolicy =
        $"default-src 'none'; script-src 'sha256-{Hash(Script)}'; style-src 'sha256-{Hash(Style)}'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>The script of the page, exactly as it is inlined (so a test can check its hash against the policy).</summary>
    public static string ScriptText => Script;

    /// <summary>The style of the page, exactly as it is inlined.</summary>
    public static string StyleText => Style;

    static string Resource(string name)
    {
        var assembly = typeof(HandOffPage).Assembly;
        using var stream = assembly.GetManifestResourceStream("BringYourOwnAgent.Relay." + name)
            ?? throw new InvalidOperationException($"the embedded resource {name} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n").Trim('\n') + "\n";
    }

    static string Hash(string text) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The page. <paramref name="mode"/> is <c>token</c> (opened from a QR code: the token is the last part of the address) or
    /// <c>short</c> (the page where a short code is typed).</summary>
    /// <param name="labels">The words.</param>
    /// <param name="mode"><c>token</c> or <c>short</c>.</param>
    /// <param name="postPath">The path the page posts to (the site's <see cref="RelayOptions.HandOffPath"/>).</param>
    /// <param name="webWallet">The web wallet's address, or null.</param>
    public static string Render(HandOffLabels labels, string mode, string postPath, string? webWallet)
    {
        static string H(string text) => WebUtility.HtmlEncode(text);
        var sb = new StringBuilder(4096);
        sb.Append("<!doctype html>\n<html lang=\"").Append(H(labels.Lang)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta name=\"robots\" content=\"noindex, nofollow\">\n<meta name=\"referrer\" content=\"no-referrer\">\n<meta name=\"color-scheme\" content=\"light dark\">\n");
        sb.Append("<title>").Append(H(labels.Title)).Append("</title>\n<style>").Append(Style).Append("</style>\n</head>\n");
        sb.Append("<body data-mode=\"").Append(mode).Append("\" data-post=\"").Append(H(postPath)).Append("\" data-web=\"").Append(H(webWallet ?? "")).Append('"');
        sb.Append(" data-used=\"").Append(H(labels.ErrorUsed)).Append("\" data-gone=\"").Append(H(labels.ErrorGone)).Append("\" data-wrong=\"").Append(H(labels.ErrorWrong));
        sb.Append("\" data-slow=\"").Append(H(labels.ErrorSlow)).Append("\" data-network=\"").Append(H(labels.ErrorNetwork)).Append("\" data-working=\"").Append(H(labels.Working));
        sb.Append("\" data-opened=\"").Append(H(labels.Opened)).Append("\">\n<main>\n<h1>").Append(H(labels.Title)).Append("</h1>\n");
        sb.Append("<p>").Append(H(labels.Explain)).Append("</p>\n");
        if (mode == "short")
        {
            sb.Append("<h2>").Append(H(labels.ShortTitle)).Append("</h2>\n");
            sb.Append("<p><label for=\"short\">").Append(H(labels.ShortLabel)).Append("</label> <input id=\"short\" type=\"text\" inputmode=\"text\" autocomplete=\"off\" autocapitalize=\"characters\" spellcheck=\"false\" maxlength=\"20\" aria-describedby=\"hint\"></p>\n");
            sb.Append("<p id=\"hint\" class=\"hint\">").Append(H(labels.ShortHint)).Append("</p>\n");
        }
        sb.Append("<p><button id=\"open\" type=\"button\" class=\"primary\" disabled>").Append(H(labels.Open)).Append("</button></p>\n");
        sb.Append("<p><button id=\"web\" type=\"button\" class=\"small\" hidden>").Append(H(labels.OpenWeb)).Append("</button></p>\n");
        sb.Append("<p id=\"msg\" role=\"status\" aria-live=\"polite\"></p>\n");
        sb.Append("<noscript><p>").Append(H(labels.NoScript)).Append("</p></noscript>\n</main>\n<script>").Append(Script).Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }
}
