using System.Globalization;
using Byoa.Wallet.Engine;

namespace Byoa.Wallet.Direct;

/// <summary>
/// A checked <c>bringyourownagent://connect?v=0.1&amp;endpoint=…&amp;manifest=…&amp;session=…</c> link (the direct
/// transport, CONTRACT-DIRECT.md §1). The endpoint is a <c>wss://</c> URL, the manifest an <c>https://</c> URL on the
/// same host and port; the site's identity is the origin <c>https://host[:port]</c>. Hosts that are IP literals,
/// <c>localhost</c>, <c>.local</c> names and other local names are refused here; addresses are checked again when
/// the wallet connects (<see cref="DirectNet"/>). With the developer switch <see cref="DevLocalVariable"/>=1 (tests
/// only) <c>ws://localhost</c> and <c>http://localhost</c> are allowed as well.
/// </summary>
public sealed record DirectLink(Uri Endpoint, Uri Manifest, string Session, string Origin, string Host)
{
    public const string Scheme = "bringyourownagent";

    /// <summary>The developer switch for tests: <c>BYOA_WALLET_DEV_LOCAL=1</c> allows <c>ws://localhost</c> and <c>http://localhost</c>.</summary>
    public const string DevLocalVariable = "BYOA_WALLET_DEV_LOCAL";

    public const int MaxLength = 4096;
    const int MaxUrlLength = 2048;
    const int MaxParameters = 16;

    public static bool DevLocalFromEnvironment => Environment.GetEnvironmentVariable(DevLocalVariable) == "1";

    /// <summary>Parses and checks a link. On failure <paramref name="why"/> says why, in a few words (for the activity log).</summary>
    public static bool TryParse(string? text, bool devLocal, out DirectLink? link, out string why)
    {
        link = null;
        if (string.IsNullOrEmpty(text)) return Fail("an empty link", out why);
        if (text.Length > MaxLength) return Fail("the link is too long", out why);
        foreach (var c in text)
        {
            if (c <= ' ' || c >= 0x7F) return Fail("the link has characters a link cannot have", out why);
        }

        var colon = text.IndexOf(':');
        if (colon < 0 || !text[..colon].Equals(Scheme, StringComparison.OrdinalIgnoreCase)) return Fail("not a bringyourownagent link", out why);
        if (text.Contains('#')) return Fail("the link has a fragment", out why);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return Fail("not a well-formed link", out why);
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.Port != -1) return Fail("not a well-formed link", out why);
        if (!uri.Host.Equals("connect", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath is not ("" or "/"))
        {
            return Fail("an unknown kind of link", out why);
        }

        var question = text.IndexOf('?');
        if (question < 0) return Fail("the link has no parameters", out why);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = text[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > MaxParameters) return Fail("the link has too many parameters", out why);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            string value;
            try
            {
                value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            catch (UriFormatException)
            {
                return Fail("the link is badly encoded", out why);
            }

            if (!parameters.TryAdd(key, value)) return Fail("the link repeats the parameter " + Safe(key), out why);
        }

        if (!parameters.TryGetValue("v", out var version)) return Fail("the link has no version", out why);
        if (version != "0.1") return Fail("an unsupported link version", out why);
        if (!parameters.TryGetValue("session", out var session) || !IsSessionCode(session)) return Fail("the link has no valid session code", out why);
        if (!parameters.TryGetValue("endpoint", out var endpointText)) return Fail("the link has no endpoint", out why);
        if (!parameters.TryGetValue("manifest", out var manifestText)) return Fail("the link has no manifest", out why);
        if (!TryTarget(endpointText, manifestText, devLocal, out var endpoint, out var manifest, out var origin, out var host, out why)) return false;

        link = new DirectLink(endpoint!, manifest!, session, origin!, host!);
        why = "";
        return true;
    }

    /// <summary>
    /// Checks an endpoint and a manifest URL (also for a stored pairing, before the wallet reconnects) and gives the
    /// site's origin and the host shown to the visitor.
    /// </summary>
    public static bool TryTarget(string endpointText, string manifestText, bool devLocal,
        out Uri? endpoint, out Uri? manifest, out string? origin, out string? host, out string why)
    {
        endpoint = manifest = null;
        origin = host = null;
        if (!TryUrl(endpointText, websocket: true, devLocal, out endpoint, out why)) return false;
        if (!TryUrl(manifestText, websocket: false, devLocal, out manifest, out why)) return false;
        var secure = endpoint!.Scheme == "wss";
        if (secure != (manifest!.Scheme == Uri.UriSchemeHttps)) return Fail("the endpoint and the manifest differ in security", out why);
        if (!endpoint.IdnHost.Equals(manifest.IdnHost, StringComparison.OrdinalIgnoreCase) || endpoint.Port != manifest.Port)
        {
            return Fail("the manifest is not on the endpoint's host and port", out why);
        }

        var name = endpoint.IdnHost.ToLowerInvariant();
        var defaultPort = secure ? 443 : 80;
        host = endpoint.Port == defaultPort ? name : name + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
        origin = (secure ? "https://" : "http://") + host;
        if (!Origins.TryParse(origin, out _, out _)) return Fail("the site's origin is not one the wallet serves", out why);
        why = "";
        return true;
    }

    static bool TryUrl(string text, bool websocket, bool devLocal, out Uri? uri, out string why)
    {
        uri = null;
        var what = websocket ? "endpoint" : "manifest";
        if (text.Length == 0 || text.Length > MaxUrlLength) return Fail("the " + what + " URL is empty or too long", out why);
        foreach (var c in text)
        {
            if (c <= ' ' || c >= 0x7F || c == '\\') return Fail("the " + what + " URL has characters a URL cannot have", out why);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return Fail("the " + what + " is not a URL", out why);
        var secureScheme = websocket ? "wss" : Uri.UriSchemeHttps;
        var plainScheme = websocket ? "ws" : Uri.UriSchemeHttp;
        var isLocalhost = uri.HostNameType == UriHostNameType.Dns && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme == plainScheme)
        {
            if (!(devLocal && isLocalhost)) return Fail("the " + what + " is not " + secureScheme + "://", out why);
        }
        else if (uri.Scheme != secureScheme)
        {
            return Fail("the " + what + " is not " + secureScheme + "://", out why);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)) return Fail("the " + what + " URL has a user name", out why);
        if (uri.Fragment.Length > 0 || text.Contains('#')) return Fail("the " + what + " URL has a fragment", out why);
        if (devLocal && isLocalhost)
        {
            why = "";
            return true;
        }

        return CheckHost(uri, what, out why);
    }

    /// <summary>Only public DNS names: no IP literals, no localhost, no .local and other local-only names, no single labels.</summary>
    static bool CheckHost(Uri uri, string what, out string why)
    {
        switch (uri.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                return Fail("the " + what + " host is an IP address", out why);
            case UriHostNameType.Dns:
                break;
            default:
                return Fail("the " + what + " host is not a DNS name", out why);
        }

        var name = uri.IdnHost.ToLowerInvariant();
        if (name.Length > 253 || name.EndsWith('.')) return Fail("the " + what + " host is not a usual DNS name", out why);
        if (name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal)) return Fail("the " + what + " host is localhost", out why);
        foreach (var local in new[] { ".local", ".home.arpa", ".internal", ".lan", ".intranet" })
        {
            if (name.EndsWith(local, StringComparison.Ordinal)) return Fail("the " + what + " host is a local network name", out why);
        }

        var labels = name.Split('.');
        if (labels.Length < 2) return Fail("the " + what + " host is a single-label (local) name", out why);
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return Fail("the " + what + " host is not a usual DNS name", out why);
            foreach (var c in label)
            {
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return Fail("the " + what + " host is not a usual DNS name", out why);
            }
        }

        // An all-numeric last label would be an IP address in another spelling (for example 2130706433 or 0x7f.1).
        if (labels[^1].All(char.IsAsciiDigit)) return Fail("the " + what + " host is an IP address", out why);
        why = "";
        return true;
    }

    /// <summary>At least 128 bits in URL-safe characters: 22 or more of A-Z a-z 0-9 - _ (at most 256).</summary>
    public static bool IsSessionCode(string? code)
    {
        if (code is null || code.Length is < 22 or > 256) return false;
        foreach (var c in code)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        }

        return true;
    }

    static string Safe(string text) => text.Length > 24 ? text[..24] : text;

    static bool Fail(string reason, out string why)
    {
        why = reason;
        return false;
    }
}
