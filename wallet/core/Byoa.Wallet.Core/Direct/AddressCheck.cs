using System.Net;
using System.Net.Sockets;

namespace Byoa.Wallet.Direct;

/// <summary>
/// Which addresses the direct transport may connect to: public unicast only. Private (RFC 1918, unique local),
/// loopback, link-local, carrier-grade NAT, unspecified, multicast, documentation and other special ranges are
/// refused, also when they hide inside an IPv6 address (IPv4-mapped, NAT64, 6to4, Teredo).
/// </summary>
public static class AddressCheck
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicV4(address.MapToIPv4());
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),
            _ => false,
        };
    }

    static bool IsPublicV4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return !(b[0] == 0                                     // "this network"
            || b[0] == 10                                      // private
            || (b[0] == 100 && (b[1] & 0xC0) == 64)            // carrier-grade NAT 100.64.0.0/10
            || b[0] == 127                                     // loopback
            || (b[0] == 169 && b[1] == 254)                    // link-local
            || (b[0] == 172 && (b[1] & 0xF0) == 16)            // private 172.16.0.0/12
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)         // IETF protocol assignments
            || (b[0] == 192 && b[1] == 0 && b[2] == 2)         // documentation
            || (b[0] == 192 && b[1] == 88 && b[2] == 99)       // 6to4 relay anycast
            || (b[0] == 192 && b[1] == 168)                    // private
            || (b[0] == 198 && (b[1] & 0xFE) == 18)            // benchmarking 198.18.0.0/15
            || (b[0] == 198 && b[1] == 51 && b[2] == 100)      // documentation
            || (b[0] == 203 && b[1] == 0 && b[2] == 113)       // documentation
            || b[0] >= 224);                                   // multicast, reserved, broadcast
    }

    static bool IsPublicV6(IPAddress address)
    {
        var b = address.GetAddressBytes();
        // Global unicast is 2000::/3; everything else (::1, ::, fc00::/7, fe80::/10, fec0::/10, ff00::/8, …) is not public.
        if ((b[0] & 0xE0) != 0x20) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;   // documentation 2001:db8::/32
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;   // Teredo 2001::/32 (hides an IPv4 address)
        if (b[0] == 0x20 && b[1] == 0x02) return false;                                   // 6to4 2002::/16 (hides an IPv4 address)
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) == 0x10) return false;   // ORCHIDv2 and similar 2001:10::/28
        return true;
    }
}
