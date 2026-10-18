using System.Text.RegularExpressions;

namespace UpdateCoordinator.Server.Assignment;

/// <summary>Wildcard patterns of administrative rules: * matches any characters, ? one character, everything else is literal.</summary>
internal static class Wildcard
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public static bool Matches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, RegexTimeout);
    }
}
