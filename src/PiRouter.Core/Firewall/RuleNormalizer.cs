using System.Globalization;
using System.Text;

namespace PiRouter.Core.Firewall;

/// <summary>
/// Reduces an iptables rule to a canonical form so a rule we wrote can be compared with the
/// same rule read back.
///
/// iptables does not echo back what you gave it. It reorders arguments, expands bare
/// addresses to /32, rewrites <c>--set-mark 100</c> as <c>--set-xmark 0x64/0xffffffff</c>
/// and inserts an implicit <c>-m tcp</c> after <c>-p tcp</c>. Comparing the raw strings
/// therefore reports drift on every single rule, forever — which would have the reconciler
/// rewriting the whole firewall every 15 seconds and would drown any real drift in noise.
/// </summary>
public static class RuleNormalizer
{
    /// <summary>
    /// Canonical form: atoms sorted so argument order stops mattering, values normalised so
    /// equivalent spellings collapse together.
    /// </summary>
    public static string Canonicalize(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return string.Empty;

        var tokens = rule.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var atoms = new List<string>();

        var current = new List<string>();
        var negated = false;

        void Flush()
        {
            if (current.Count == 0) return;
            atoms.Add(Normalise(current, negated));
            current = [];
            negated = false;
        }

        foreach (var token in tokens)
        {
            if (token == "!")
            {
                // A negation belongs to the atom that follows it.
                if (current.Count > 0) Flush();
                negated = true;
                continue;
            }

            if (token.StartsWith('-') && !IsValueLikeNegativeNumber(token))
            {
                Flush();
                current.Add(token);
            }
            else
            {
                if (current.Count == 0) current.Add(token);
                else current.Add(token);
            }
        }
        Flush();

        // "-m tcp" is added implicitly by iptables whenever "-p tcp" is present, so it
        // carries no information and is dropped from both sides.
        atoms.RemoveAll(a => a is "-m tcp");

        atoms.Sort(StringComparer.Ordinal);
        return string.Join(' ', atoms);
    }

    /// <summary>True when two rules mean the same thing however they happen to be spelled.</summary>
    public static bool AreEquivalent(string a, string b) =>
        string.Equals(Canonicalize(a), Canonicalize(b), StringComparison.Ordinal);

    private static string Normalise(List<string> atom, bool negated)
    {
        var flag = atom[0];
        var values = atom.Skip(1).ToList();

        switch (flag)
        {
            // Bare addresses are echoed back with an explicit prefix length.
            case "-s" or "-d" or "--source" or "--destination":
                values = [.. values.Select(WithPrefix)];
                break;

            // --set-mark N is stored, and reported, as an xmark with a full mask.
            case "--set-mark":
                if (values.Count == 1 && TryParseMark(values[0], out var mark))
                {
                    flag = "--set-xmark";
                    values = [$"0x{mark:x}/0xffffffff"];
                }
                break;

            case "--set-xmark":
                if (values.Count == 1) values = [NormaliseXmark(values[0])];
                break;

            // Mark matches are reported in hex.
            case "--mark":
                if (values.Count == 1 && TryParseMark(values[0], out var m2))
                    values = [$"0x{m2:x}"];
                break;
        }

        var builder = new StringBuilder();
        if (negated) builder.Append("! ");
        builder.Append(flag);
        foreach (var value in values) builder.Append(' ').Append(value);
        return builder.ToString();
    }

    private static string WithPrefix(string address) =>
        address.Contains('/') ? address : $"{address}/32";

    private static string NormaliseXmark(string value)
    {
        var parts = value.Split('/');
        var mark = TryParseMark(parts[0], out var v) ? v : 0;
        var mask = parts.Length > 1 && TryParseMark(parts[1], out var mk) ? mk : 0xffffffff;
        return $"0x{mark:x}/0x{mask:x}";
    }

    private static bool TryParseMark(string value, out long result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            result = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt64(value[2..], 16)
                : long.Parse(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Distinguishes a flag from a negative number appearing as a value.</summary>
    private static bool IsValueLikeNegativeNumber(string token) =>
        token.Length > 1 && char.IsDigit(token[1]);
}
