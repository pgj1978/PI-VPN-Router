using System.Globalization;
using System.Text.RegularExpressions;

namespace PiRouter.Core.Firewall;

/// <summary>
/// Reduces a policy-routing rule to a comparable identity of (negated, fwmark, table).
///
/// This decides which live rules the applier is willing to delete, so getting it wrong
/// means tearing down somebody else's routing. It lives on its own, public and pure,
/// specifically so that it can be tested against real `ip rule show` output.
/// </summary>
public static partial class IpRuleSignature
{
    /// <summary>Returns null when the rule has no fwmark/lookup pair, meaning we do not own it.</summary>
    public static string? From(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var mark = FwmarkPattern().Match(body);
        var lookup = LookupPattern().Match(body);
        if (!mark.Success || !lookup.Success) return null;

        var negated = body.TrimStart().StartsWith("not ", StringComparison.Ordinal);
        return $"{(negated ? "not:" : "")}{Normalise(mark.Groups["mark"].Value)}->{lookup.Groups["table"].Value}";
    }

    public static string? From(IReadOnlyList<string> selector) => From(string.Join(' ', selector));

    /// <summary>Rebuilds an `ip rule` selector from a signature so the rule can be deleted.</summary>
    public static string[] ToSelector(string signature)
    {
        var negated = signature.StartsWith("not:", StringComparison.Ordinal);
        var parts = (negated ? signature[4..] : signature).Split("->");
        return negated
            ? ["not", "fwmark", parts[0], "lookup", parts[1]]
            : ["fwmark", parts[0], "lookup", parts[1]];
    }

    /// <summary>
    /// Marks are printed in hex by iproute2 but accepted in decimal, so they are normalised
    /// to decimal for comparison. A masked mark keeps its mask verbatim: Tailscale's
    /// "0x80000/0xff0000" must never compare equal to a plain mark of ours.
    /// </summary>
    private static string Normalise(string mark)
    {
        if (mark.Contains('/')) return mark;
        return mark.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(mark[2..], 16).ToString(CultureInfo.InvariantCulture)
            : mark;
    }

    [GeneratedRegex(@"fwmark\s+(?<mark>\S+)")]
    private static partial Regex FwmarkPattern();

    [GeneratedRegex(@"lookup\s+(?<table>\S+)")]
    private static partial Regex LookupPattern();
}
