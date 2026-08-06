using System.Net;
using System.Net.Sockets;

namespace PiRouter.Core.Net;

/// <summary>Small CIDR helpers. Pure, so they are cheap to test.</summary>
public static class Cidr
{
    /// <summary>"192.168.20.1/24" -> "192.168.20.1". A bare address is returned unchanged.</summary>
    public static string AddressOf(string cidr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidr);
        var slash = cidr.IndexOf('/');
        return slash < 0 ? cidr.Trim() : cidr[..slash].Trim();
    }

    /// <summary>"192.168.20.1/24" -> 24. Defaults to 24 when no prefix is present.</summary>
    public static int PrefixOf(string cidr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidr);
        var slash = cidr.IndexOf('/');
        if (slash < 0) return 24;
        return int.TryParse(cidr[(slash + 1)..].Trim(), out var p) && p is >= 0 and <= 32 ? p : 24;
    }

    /// <summary>"192.168.20.1/24" -> "192.168.20.0/24".</summary>
    public static string NetworkOf(string cidr)
    {
        var prefix = PrefixOf(cidr);
        var address = IPAddress.Parse(AddressOf(cidr));
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"Only IPv4 is supported: {cidr}", nameof(cidr));

        var bytes = address.GetAddressBytes();
        var mask = MaskBytes(prefix);
        for (var i = 0; i < 4; i++) bytes[i] &= mask[i];
        return $"{new IPAddress(bytes)}/{prefix}";
    }

    /// <summary>Prefix length -> dotted mask, e.g. 24 -> "255.255.255.0".</summary>
    public static string PrefixToMask(int prefix)
    {
        if (prefix is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefix));
        return new IPAddress(MaskBytes(prefix)).ToString();
    }

    /// <summary>Dotted mask -> prefix length. Returns -1 for a non-contiguous or invalid mask.</summary>
    public static int MaskToPrefix(string mask)
    {
        if (!IPAddress.TryParse(mask, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            return -1;

        var prefix = 0;
        var seenZero = false;
        foreach (var b in parsed.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if (((b >> bit) & 1) == 1)
                {
                    if (seenZero) return -1; // holes in the mask are invalid
                    prefix++;
                }
                else seenZero = true;
            }
        }
        return prefix;
    }

    /// <summary>True when <paramref name="address"/> falls inside <paramref name="cidr"/>.</summary>
    public static bool Contains(string cidr, string address)
    {
        if (!IPAddress.TryParse(AddressOf(cidr), out var net)) return false;
        if (!IPAddress.TryParse(address, out var candidate)) return false;
        if (net.AddressFamily != AddressFamily.InterNetwork ||
            candidate.AddressFamily != AddressFamily.InterNetwork) return false;

        var mask = MaskBytes(PrefixOf(cidr));
        var netBytes = net.GetAddressBytes();
        var candidateBytes = candidate.GetAddressBytes();
        for (var i = 0; i < 4; i++)
            if ((netBytes[i] & mask[i]) != (candidateBytes[i] & mask[i])) return false;
        return true;
    }

    /// <summary>
    /// Strict dotted-quad check. IPAddress.TryParse alone is too permissive for user input:
    /// it accepts "1" as 0.0.0.1 and "010.1.1.1" as octal, either of which would silently
    /// become a nonsense DHCP reservation. Requiring a clean round-trip rejects both.
    /// </summary>
    public static bool IsValidIpv4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        return IPAddress.TryParse(trimmed, out var ip)
               && ip.AddressFamily == AddressFamily.InterNetwork
               && ip.ToString() == trimmed;
    }

    private static byte[] MaskBytes(int prefix)
    {
        var bytes = new byte[4];
        for (var i = 0; i < prefix; i++) bytes[i / 8] |= (byte)(1 << (7 - i % 8));
        return bytes;
    }
}
