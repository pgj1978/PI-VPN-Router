using System.Text.RegularExpressions;

namespace PiRouter.Core.Services;

/// <summary>
/// Validates VPN profile names before they are used to build a file path.
///
/// The previous implementation passed the name straight from an unauthenticated query
/// string into Path.Combine, so a request could read or write any file the container
/// could reach. With the API deliberately left unauthenticated on the LAN, this
/// validation is the only thing standing between a URL and the filesystem.
/// </summary>
public static partial class VpnProfileName
{
    public const int MaxLength = 64;

    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxLength
        && SafePattern().IsMatch(name)
        && name != "."
        && name != "..";

    /// <summary>Resolves a profile path, throwing rather than escaping the profile directory.</summary>
    public static string ResolvePath(string directory, string name, string extension = ".conf")
    {
        if (!IsValid(name))
            throw new ArgumentException(
                $"Invalid profile name '{name}'. Use letters, digits, dots, dashes and underscores only.",
                nameof(name));

        var full = Path.GetFullPath(Path.Combine(directory, name + extension));
        var root = Path.GetFullPath(directory);

        // Belt and braces: even with a validated name, confirm the result stayed inside.
        if (!full.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new ArgumentException($"Profile name '{name}' escapes the profile directory", nameof(name));

        return full;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafePattern();
}
