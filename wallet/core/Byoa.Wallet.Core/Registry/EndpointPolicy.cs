using System.Net;
using System.Net.Sockets;

namespace Byoa.Wallet.Registry;

/// <summary>Pure admission checks for endpoints supplied by an agent.</summary>
public static class EndpointPolicy
{
    public static EndpointAssessment Check(string? value)
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

            var host = uri.DnsSafeHost;
            if (string.IsNullOrWhiteSpace(host))
                return EndpointAssessment.Refused("The endpoint must contain a host.");
            if (host.EndsWith(".", StringComparison.Ordinal) || host.Contains("..", StringComparison.Ordinal))
                return EndpointAssessment.Refused("The endpoint host has an unsafe trailing or repeated dot.");

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return new EndpointAssessment(true, true, false, uri, null);

            if (IPAddress.TryParse(host, out var address))
            {
                if (IsForbiddenAddress(address))
                    return EndpointAssessment.Refused("The endpoint address is a link-local, multicast, broadcast, or unspecified address.");
                if (IsLoopback(address))
                    return new EndpointAssessment(true, true, false, uri, null);
                return new EndpointAssessment(true, false, true, uri, uri.Scheme == "http" ? "not encrypted" : null);
            }

            // A single-label name can be a surprising local alias. localhost is the only one allowed without a dot.
            if (!host.Contains('.', StringComparison.Ordinal))
                return EndpointAssessment.Refused("Use localhost or a host name with a dot in it.");

            return new EndpointAssessment(true, false, true, uri, uri.Scheme == "http" ? "not encrypted" : null);
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
    public static async Task<EndpointAssessment> CheckForConfirmationAsync(string value, CancellationToken ct = default)
    {
        var assessment = Check(value);
        if (!assessment.Allowed || !assessment.NeedsConfirmation || assessment.Uri is null ||
            IPAddress.TryParse(assessment.Uri.DnsSafeHost, out _))
            return assessment;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(assessment.Uri.DnsSafeHost)
                .WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (addresses.Any(IsForbiddenAddress))
                return EndpointAssessment.Refused("The endpoint name resolves to a link-local, multicast, broadcast, or unspecified address.");
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
}

public sealed record EndpointAssessment(bool Allowed, bool IsLoopback, bool NeedsConfirmation, Uri? Uri, string? Warning)
{
    public static EndpointAssessment Refused(string message) => new(false, false, false, null, message);
    public string Message => Allowed ? "allowed" : Warning ?? "The endpoint was refused.";
}
