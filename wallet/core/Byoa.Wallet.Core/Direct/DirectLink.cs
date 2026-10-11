using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Engine;

namespace Byoa.Wallet.Direct;

/// <summary>
/// A checked <c>bringyourownagent://connect?v=0.1&amp;endpoint=…&amp;manifest=…&amp;session=…</c> link (the direct
/// transport, CONTRACT-DIRECT.md §1 as amended by §7). The endpoint is a <c>wss://</c> URL; the manifest travels in
/// the link itself, as base64url (no padding) of its JSON, at most 4096 bytes, so the wallet needs no network request
/// before the visitor's Allow. The site's identity is the origin <c>https://host[:port]</c> of the endpoint. Hosts
/// that are IP literals, <c>localhost</c>, <c>.local</c> names and other local names are refused here; addresses are
/// checked again when the wallet connects (<see cref="DirectNet"/>). With the developer switch
/// <see cref="DevLocalVariable"/>=1 (tests only) <c>ws://localhost</c> is allowed as well.
/// </summary>
/// <param name="Manifest">The manifest exactly as the link carries it (base64url): the site's welcome must echo it.</param>
/// <param name="ManifestJson">The decoded manifest, shown in the consent.</param>
public sealed record DirectLink(Uri Endpoint, string Manifest, JsonObject ManifestJson, string Session, string Origin, string Host)
{
    public const string Scheme = "bringyourownagent";

    /// <summary>The developer switch for tests: <c>BYOA_WALLET_DEV_LOCAL=1</c> allows <c>ws://localhost</c>.</summary>
    public const string DevLocalVariable = "BYOA_WALLET_DEV_LOCAL";

    /// <summary>The manifest's limit, decoded (CONTRACT-DIRECT.md §7).</summary>
    public const int MaxManifestBytes = 4096;

    /// <summary>Base64url without padding of <see cref="MaxManifestBytes"/> bytes.</summary>
    const int MaxManifestChars = (MaxManifestBytes * 4 + 2) / 3;

    /// <summary>The whole link: room for an escaped endpoint, the manifest and the code.</summary>
    public const int MaxLength = 12288;

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
        if (!TryManifest(manifestText, out var manifestJson, out why)) return false;
        if (!TryTarget(endpointText, devLocal, out var endpoint, out var origin, out var host, out why)) return false;

        link = new DirectLink(endpoint!, manifestText, manifestJson!, session, origin!, host!);
        why = "";
        return true;
    }

    /// <summary>
    /// Decodes a manifest as the link carries it: base64url without padding, at most 4096 bytes, a JSON object.
    /// (Whether it is a valid BYOA manifest is the engine's check, with the .NET library.)
    /// </summary>
    public static bool TryManifest(string? text, out JsonObject? manifest, out string why)
    {
        manifest = null;
        if (string.IsNullOrEmpty(text)) return Fail("the link has no manifest", out why);
        if (text.Length > MaxManifestChars) return Fail("the manifest is larger than 4096 bytes", out why);
        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return Fail("the manifest is not base64url without padding", out why);
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return Fail("the manifest is not base64url without padding", out why);
        }

        if (bytes.Length > MaxManifestBytes) return Fail("the manifest is larger than 4096 bytes", out why);
        try
        {
            manifest = JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException)
        {
            return Fail("the manifest is not JSON", out why);
        }

        if (manifest is null) return Fail("the manifest is not a JSON object", out why);
        why = "";
        return true;
    }

    /// <summary>The form a site puts in the link (and echoes in its welcome): base64url of the JSON bytes, no padding.</summary>
    public static string EncodeManifest(byte[] json) => Base64Url.EncodeToString(json);

    /// <summary>
    /// Checks an endpoint (also for a stored pairing, before the wallet reconnects) and gives the site's origin and the
    /// host shown to the visitor.
    /// </summary>
    public static bool TryTarget(string endpointText, bool devLocal, out Uri? endpoint, out string? origin, out string? host, out string why)
    {
        origin = host = null;
        if (!TryUrl(endpointText, devLocal, out endpoint, out why)) return false;
        var secure = endpoint!.Scheme == "wss";
        var name = endpoint.IdnHost.ToLowerInvariant();
        var defaultPort = secure ? 443 : 80;
        host = endpoint.Port == defaultPort ? name : name + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
        origin = (secure ? "https://" : "http://") + host;
        if (!Origins.TryParse(origin, out _, out _)) return Fail("the site's origin is not one the wallet serves", out why);
        why = "";
        return true;
    }

    static bool TryUrl(string text, bool devLocal, out Uri? uri, out string why)
    {
        uri = null;
        if (text.Length == 0 || text.Length > MaxUrlLength) return Fail("the endpoint URL is empty or too long", out why);
        foreach (var c in text)
        {
            if (c <= ' ' || c >= 0x7F || c == '\\') return Fail("the endpoint URL has characters a URL cannot have", out why);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return Fail("the endpoint is not a URL", out why);
        var isLocalhost = uri.HostNameType == UriHostNameType.Dns && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme == "ws")
        {
            if (!(devLocal && isLocalhost)) return Fail("the endpoint is not wss://", out why);
        }
        else if (uri.Scheme != "wss")
        {
            return Fail("the endpoint is not wss://", out why);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)) return Fail("the endpoint URL has a user name", out why);
        if (uri.Fragment.Length > 0 || text.Contains('#')) return Fail("the endpoint URL has a fragment", out why);
        if (devLocal && isLocalhost)
        {
            why = "";
            return true;
        }

        return CheckHost(uri, out why);
    }

    /// <summary>Only public DNS names: no IP literals, no localhost, no .local and other local-only names, no single labels.</summary>
    static bool CheckHost(Uri uri, out string why)
    {
        switch (uri.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                return Fail("the endpoint host is an IP address", out why);
            case UriHostNameType.Dns:
                break;
            default:
                return Fail("the endpoint host is not a DNS name", out why);
        }

        var name = uri.IdnHost.ToLowerInvariant();
        if (name.Length > 253 || name.EndsWith('.')) return Fail("the endpoint host is not a usual DNS name", out why);
        if (name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal)) return Fail("the endpoint host is localhost", out why);
        foreach (var local in new[] { ".local", ".home.arpa", ".internal", ".lan", ".intranet" })
        {
            if (name.EndsWith(local, StringComparison.Ordinal)) return Fail("the endpoint host is a local network name", out why);
        }

        var labels = name.Split('.');
        if (labels.Length < 2) return Fail("the endpoint host is a single-label (local) name", out why);
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return Fail("the endpoint host is not a usual DNS name", out why);
            foreach (var c in label)
            {
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return Fail("the endpoint host is not a usual DNS name", out why);
            }
        }

        // An all-numeric last label would be an IP address in another spelling (for example 2130706433 or 0x7f.1).
        if (labels[^1].All(char.IsAsciiDigit)) return Fail("the endpoint host is an IP address", out why);
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
