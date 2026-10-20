using System.Globalization;
using System.Text.RegularExpressions;

namespace SUSyphos.Server.AgentPackages;

/// <summary>
/// Semantic version of an agent ("0.3.0", "0.4.0-rc.1", build metadata after "+" is ignored), ordered by the SemVer rules,
/// so the central server can tell which agents are older than a released package.
/// </summary>
internal sealed partial record AgentVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<AgentVersion>
{
    /// <summary>Upper bound for a version text in file names and the database.</summary>
    public const int MaxLength = 40;

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AgentVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text) || text.Length > 128)
        {
            return false;
        }

        var match = VersionRegex().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        version = new AgentVersion(major, minor, patch, prerelease);
        return version.ToString().Length <= MaxLength;
    }

    /// <summary>
    /// Short form for lists and filters: major.minor.patch without prerelease and build metadata (e.g. "0.5.0"); the full
    /// version stays available as tooltip. Text that is no version is returned as it is.
    /// </summary>
    public static string Short(string? text) => TryParse(text, out var version)
        ? string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Patch}")
        : text ?? string.Empty;

    /// <summary>Sort key of the short form, so 0.10.0 sorts after 0.9.0; text that is no version sorts first.</summary>
    public static long SortKey(string? text) => TryParse(text, out var version)
        ? (Math.Min(version.Major, 999_999L) * 1_000_000_000_000L) + (Math.Min(version.Minor, 999_999L) * 1_000_000L) + Math.Min(version.Patch, 999_999L)
        : -1;

    /// <summary>First agent version that understands week rules of maintenance windows (monthly windows).</summary>
    public const string WeekRulesSince = "0.7.1";

    /// <summary>
    /// True if the agent understands week rules; development builds of that version count (prerelease is ignored), unknown
    /// versions do not.
    /// </summary>
    public static bool SupportsWeekRules(string? agentVersion) => SortKey(agentVersion) >= SortKey(WeekRulesSince);

    public int CompareTo(AgentVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (result != 0)
        {
            return result;
        }

        // A version without prerelease ranks above every prerelease of it.
        if (Prerelease is null || other.Prerelease is null)
        {
            return (Prerelease is null ? 1 : 0) - (other.Prerelease is null ? 1 : 0);
        }

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var part = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i]),
            };
            if (part != 0)
            {
                return part;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(AgentVersion left, AgentVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AgentVersion left, AgentVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AgentVersion left, AgentVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AgentVersion left, AgentVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => Prerelease is null
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}");

    [GeneratedRegex(@"^(?<major>0|[1-9][0-9]{0,8})\.(?<minor>0|[1-9][0-9]{0,8})\.(?<patch>0|[1-9][0-9]{0,8})(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VersionRegex();
}
