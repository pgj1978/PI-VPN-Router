namespace PiRouter.Core.Net;

/// <summary>
/// Normalises MAC addresses to lowercase colon-separated form.
///
/// This is the canonical form dnsmasq writes into its lease file, and devices are keyed on
/// MAC throughout. A device whose MAC is stored in a different case or separator style
/// simply never matches its lease, and its bypass setting silently does nothing.
/// </summary>
public static class MacAddress
{
    public static bool TryNormalise(string? input, out string normalised)
    {
        normalised = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var decoded = Uri.UnescapeDataString(input).Trim();

        Span<char> hex = stackalloc char[12];
        var count = 0;
        foreach (var c in decoded)
        {
            if (c is ':' or '-' or '.' or ' ') continue;
            if (!Uri.IsHexDigit(c) || count == 12) return false;
            hex[count++] = char.ToLowerInvariant(c);
        }

        if (count != 12) return false;

        Span<char> result = stackalloc char[17];
        for (var i = 0; i < 6; i++)
        {
            result[i * 3] = hex[i * 2];
            result[i * 3 + 1] = hex[i * 2 + 1];
            if (i < 5) result[i * 3 + 2] = ':';
        }

        normalised = new string(result);
        return true;
    }

    public static string? Normalise(string? input) =>
        TryNormalise(input, out var normalised) ? normalised : null;
}
