using System.Globalization;

namespace SUSyphos.Server.Agents;

/// <summary>Readable names of the supported Windows Server versions, derived from the build the agent reports.</summary>
internal static class OperatingSystems
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [14393] = "Windows Server 2016",
        [17763] = "Windows Server 2019",
        [20348] = "Windows Server 2022",
        [26100] = "Windows Server 2025",
    };

    /// <summary>The build number (third part of e.g. 10.0.17763.6414), or null if the version is not a Windows version.</summary>
    public static int? BuildNumber(string? osVersion)
    {
        var parts = osVersion?.Split('.');
        return parts is { Length: >= 3 } && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var build) ? build : null;
    }

    /// <summary>Build and update revision, e.g. 17763.6414.</summary>
    public static string Build(string? osVersion)
    {
        var parts = osVersion?.Split('.');
        return parts is { Length: >= 4 } ? $"{parts[2]}.{parts[3]}" : parts is { Length: 3 } ? parts[2] : osVersion ?? "–";
    }

    /// <summary>
    /// E.g. "Windows Server 2019". Known server builds win over the product name the agent reported, because that name
    /// carries the edition and may be localized; unknown builds show the reported name or the build.
    /// </summary>
    public static string Name(string? productName, string? osVersion)
    {
        if (BuildNumber(osVersion) is { } build && Names.TryGetValue(build, out var name))
        {
            return name;
        }

        if (!string.IsNullOrWhiteSpace(productName))
        {
            return productName.Trim();
        }

        return BuildNumber(osVersion) is { } unknown ? $"Windows (build {unknown.ToString(CultureInfo.InvariantCulture)})" : "Unknown";
    }
}
