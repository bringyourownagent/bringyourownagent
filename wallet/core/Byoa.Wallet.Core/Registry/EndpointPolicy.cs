using System.Net;
using System.Net.Sockets;

namespace Byoa.Wallet.Registry;

/// <summary>Pure admission checks for endpoints supplied by an agent.</summary>
public static class EndpointPolicy
{
    public static EndpointAssessment Check(string? value, bool agentSupplied = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                return EndpointAssessment.Refused("The endpoint is empty.");

            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
                return EndpointAssessment.Refused("The endpoint must be a complete http or https URL.");
            if (uri.Scheme is not ("http" or "https"))
                return EndpointAssessment.Refused("Only http and https endpoints are allowed.");
            if (!string.IsNullOrEmpty(uri.UserInfo))
                return EndpointAssessment.Refused("The endpoint must not contain a username or password.");
            // Uri.Port is -1 when the scheme's normal port was omitted. That is a valid endpoint; only an
            // explicitly supplied port outside the TCP range is refused.
            if (!uri.IsDefaultPort && (uri.Port is <= 0 or > 65535))
                return EndpointAssessment.Refused("The endpoint port must be between 1 and 65535.");

            var host = uri.IdnHost;
            if (string.IsNullOrWhiteSpace(host))
                return EndpointAssessment.Refused("The endpoint must contain a host.");
            if (host.EndsWith(".", StringComparison.Ordinal) || host.Contains("..", StringComparison.Ordinal))
                return EndpointAssessment.Refused("The endpoint host has an unsafe trailing or repeated dot.");

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return Allowed(uri, true, false, null, uri) with { AddressClass = "loopback" };

            if (IPAddress.TryParse(host, out var address))
            {
                if (IsForbiddenAddress(address))
                    return EndpointAssessment.Refused("The endpoint address is a link-local, multicast, broadcast, or unspecified address.");
                if (IsLoopback(address))
                    return Allowed(uri, true, false, null, uri).WithAddress(address);
                return Allowed(uri, false, true, uri.Scheme == "http" ? "not encrypted" : null, uri).WithAddress(address);
            }

            // A single-label name can be a surprising local alias. localhost is the only one allowed without a dot.
            if (!host.Contains('.', StringComparison.Ordinal) && agentSupplied)
                return EndpointAssessment.Refused("Use localhost or a host name with a dot in it.");

            return Allowed(uri, false, agentSupplied, uri.Scheme == "http" ? "not encrypted" : null, uri);
        }
        catch (UriFormatException)
        {
            return EndpointAssessment.Refused("The endpoint must be a complete http or https URL with a valid port.");
        }
        catch (ArgumentException)
        {
            return EndpointAssessment.Refused("The endpoint is not valid.");
        }
    }

    /// <summary>
    /// Performs the one DNS check permitted at the confirmation boundary. Check never contacts the host or DNS.
    /// </summary>
    public static async Task<EndpointAssessment> CheckForConfirmationAsync(string value, CancellationToken ct = default,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        var assessment = Check(value, agentSupplied: true);
        if (!assessment.Allowed || !assessment.NeedsConfirmation || assessment.Uri is null ||
            IPAddress.TryParse(assessment.Uri.DnsSafeHost, out _))
            return assessment;

        try
        {
            var resolve = resolver ?? ((string host, CancellationToken token) => Dns.GetHostAddressesAsync(host, token));
            var addresses = await resolve(assessment.Uri.IdnHost, ct)
                .WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(address => IsForbiddenAddress(address) || IsLoopback(address)))
                return EndpointAssessment.Refused("The endpoint name resolves to a loopback, link-local, multicast, broadcast, or unspecified address.");
            var distinct = addresses.Distinct().ToArray();
            var classes = distinct.Select(ClassifyAddress).Distinct(StringComparer.Ordinal).ToArray();
            return assessment with
            {
                ResolvedAddresses = distinct.Select(address => address.ToString()).ToArray(),
                AddressClass = classes.Length == 1 ? classes[0] : "mixed",
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A slow DNS server is not allowed to hold the wallet hostage. The visitor still sees the full host.
        }
        catch (TimeoutException)
        {
            // A slow DNS server is not allowed to hold the wallet hostage. The visitor still sees the full host.
        }
        catch (SocketException)
        {
            // The host may be a home server that is temporarily offline. Confirmation is still explicit.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A DNS provider must not turn an already-confirmed, explicit endpoint into a wallet crash.
        }

        return assessment;
    }

    static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address) || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));

    static bool IsForbiddenAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal || address.IsIPv6Teredo)
            return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return true;

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 0) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] >= 224 || (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255)) return true;
        }

        return false;
    }

    static EndpointAssessment Allowed(Uri uri, bool loopback, bool confirmation, string? warning, Uri original) =>
        new(true, loopback, confirmation, uri, warning)
        {
            CanonicalEndpoint = Canonical(original),
            ContainsNonAsciiHost = original.Host.Any(c => c > 127),
        };

    public static string Canonical(Uri uri)
    {
        var builder = new UriBuilder(uri) { Host = uri.IdnHost };
        return builder.Uri.ToString().TrimEnd('/');
    }

    public static bool IsLoopbackAddress(IPAddress address) => IsLoopback(address);

    public static string ClassifyAddress(IPAddress address)
    {
        if (IsLoopback(address)) return "loopback";
        return IsPrivateAddress(address) ? "private" : "public";
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        return address.IsIPv6UniqueLocal;
    }

    public static bool IsAllowedAtUse(IPAddress address, string? confirmedClass, IReadOnlyCollection<string> confirmedAddresses)
    {
        if (IsForbiddenAddress(address)) return false;
        if (confirmedClass == "loopback") return IsLoopback(address);
        if (IsLoopback(address)) return false;
        if (confirmedClass == "private") return IsPrivateAddress(address);
        if (confirmedClass == "public") return !IsPrivateAddress(address);
        // A mixed/unknown confirmation is fail-closed. A literal endpoint does not use this path.
        return false;
    }
}

public sealed record EndpointAssessment(bool Allowed, bool IsLoopback, bool NeedsConfirmation, Uri? Uri, string? Warning)
{
    public static EndpointAssessment Refused(string message) => new(false, false, false, null, message);
    public string Message => Allowed ? "allowed" : Warning ?? "The endpoint was refused.";
    public string CanonicalEndpoint { get; init; } = "";
    public IReadOnlyList<string> ResolvedAddresses { get; init; } = [];
    public string? AddressClass { get; init; }
    public bool ContainsNonAsciiHost { get; init; }

    public EndpointAssessment WithAddress(IPAddress address) => this with
    {
        ResolvedAddresses = [address.ToString()],
        AddressClass = EndpointPolicy.ClassifyAddress(address),
    };
}
