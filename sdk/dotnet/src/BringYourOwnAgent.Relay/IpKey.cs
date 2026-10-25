using System;
using System.Net;
using System.Net.Sockets;

namespace BringYourOwnAgent.Relay;

/// <summary>The address a visitor is counted under, for the per-address rate limits: IPv4 as it is, IPv6 by its /64 network (a visitor
/// owns the whole network and could change the rest at will), this computer itself as one key. Never a secret, never logged as such:
/// only used as a dictionary key.</summary>
public static class IpKey
{
    const string Loopback = "loopback";

    /// <summary>The key of one visitor address; anything that is not an address is counted as "unknown".</summary>
    public static string Of(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return "unknown";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return Loopback;
        if (a.AddressFamily != AddressFamily.InterNetworkV6) return a.ToString();
        var bytes = a.GetAddressBytes();
        return "v6:" + Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}