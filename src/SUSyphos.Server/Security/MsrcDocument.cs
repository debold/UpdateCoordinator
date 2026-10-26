using System.Globalization;
using System.Text.Json.Serialization;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Security;

/// <summary>The list of MSRC documents (GET /cvrf/v3.0/updates); only the fields the central server uses.</summary>
internal sealed class MsrcUpdateList
{
    [JsonPropertyName("value")]
    public List<MsrcUpdate> Value { get; set; } = [];
}

/// <summary>One monthly document of the list.</summary>
internal sealed class MsrcUpdate
{
    [JsonPropertyName("ID")]
    public string Id { get; set; } = string.Empty;

    public string? DocumentTitle { get; set; }

    public DateTime InitialReleaseDate { get; set; }

    public DateTime CurrentReleaseDate { get; set; }
}

/// <summary>
/// A monthly CVRF document (GET /cvrf/v3.0/cvrf/{id}, JSON); only the fields the central server uses, so the 20–30 MB
/// document is read as a stream without keeping the rest.
/// </summary>
internal sealed class MsrcDocument
{
    public MsrcTracking? DocumentTracking { get; set; }

    public MsrcProductTree? ProductTree { get; set; }

    public List<MsrcVulnerability> Vulnerability { get; set; } = [];
}

internal sealed class MsrcTracking
{
    public DateTime InitialReleaseDate { get; set; }
}

internal sealed class MsrcProductTree
{
    public List<MsrcProduct> FullProductName { get; set; } = [];
}

internal sealed class MsrcProduct
{
    public string? ProductID { get; set; }

    public string? Value { get; set; }
}

internal sealed class MsrcVulnerability
{
    public MsrcText? Title { get; set; }

    public string? CVE { get; set; }

    public List<MsrcThreat> Threats { get; set; } = [];

    public List<MsrcScore> CVSSScoreSets { get; set; } = [];

    public List<MsrcRemediation> Remediations { get; set; } = [];
}

internal sealed class MsrcText
{
    public string? Value { get; set; }
}

/// <summary>Type 0: impact, 1: exploit status ("Publicly Disclosed:No;Exploited:No;…"), 3: severity.</summary>
internal sealed class MsrcThreat
{
    public int Type { get; set; }

    public MsrcText? Description { get; set; }

    public List<string> ProductID { get; set; } = [];
}

internal sealed class MsrcScore
{
    public double BaseScore { get; set; }

    public List<string> ProductID { get; set; } = [];
}

/// <summary>Type 2 is the vendor fix: the KB in the description and the build that fixes it.</summary>
internal sealed class MsrcRemediation
{
    public int Type { get; set; }

    public MsrcText? Description { get; set; }

    public string? FixedBuild { get; set; }

    public List<string> ProductID { get; set; } = [];
}

/// <summary>What the central server keeps from one monthly document.</summary>
internal sealed record MsrcMonth(string Month, DateTime ReleasedAtUtc, IReadOnlyList<SecurityBaselineEntity> Baselines, IReadOnlyList<SecurityVulnerabilityEntity> Vulnerabilities, IReadOnlyList<SecurityFixEntity> Fixes);

/// <summary>
/// Turns a monthly CVRF document into baselines, vulnerabilities and fixes for the Windows Server versions of the lifecycle
/// table (including Server Core): per vulnerability the fixing build and KB, per version the highest build of the month.
/// </summary>
internal static class MsrcParser
{
    private const int VendorFix = 2;
    private const int ImpactThreat = 0;
    private const int ExploitThreat = 1;
    private const int SeverityThreat = 3;

    private static readonly string[] SeverityOrder = ["Low", "Moderate", "Important", "Critical"];

    public static MsrcMonth Parse(string month, MsrcDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Product IDs of the Windows Server versions, with their build ("Windows Server 2022" and its Server Core installation).
        var builds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var product in document.ProductTree?.FullProductName ?? [])
        {
            if (product.ProductID is { } id && ServerBuild(product.Value) is { } build)
            {
                builds[id] = build;
            }
        }

        var released = DateTime.SpecifyKind(document.DocumentTracking?.InitialReleaseDate ?? DateTime.MinValue, DateTimeKind.Utc);
        var vulnerabilities = new List<SecurityVulnerabilityEntity>();
        var fixes = new Dictionary<(string Cve, int Build), SecurityFixEntity>();
        foreach (var vulnerability in document.Vulnerability)
        {
            if (vulnerability.CVE is not { Length: > 0 and <= 32 } cve)
            {
                continue;
            }

            var found = false;
            foreach (var remediation in vulnerability.Remediations.Where(r => r.Type == VendorFix))
            {
                if (ParseBuild(remediation.FixedBuild) is not { } fixedBuild)
                {
                    continue;
                }

                foreach (var build in remediation.ProductID.Where(builds.ContainsKey).Select(id => builds[id]).Distinct())
                {
                    // Hotpatch and other branches report another build number; they do not count for the version.
                    if (fixedBuild.Build != build)
                    {
                        continue;
                    }

                    found = true;
                    var kb = remediation.Description?.Value is { } text && Rollouts.UpdateText.IsKb(text.Trim()) ? text.Trim() : null;
                    if (!fixes.TryGetValue((cve, build), out var known) || known.Revision < fixedBuild.Revision)
                    {
                        fixes[(cve, build)] = new SecurityFixEntity { Cve = cve, Month = month, Build = build, Revision = fixedBuild.Revision, Kb = kb };
                    }
                }
            }

            if (!found)
            {
                continue;
            }

            var serverThreats = vulnerability.Threats.Where(t => t.ProductID.Any(builds.ContainsKey)).ToList();
            var exploit = vulnerability.Threats.FirstOrDefault(t => t.Type == ExploitThreat)?.Description?.Value ?? string.Empty;
            vulnerabilities.Add(new SecurityVulnerabilityEntity
            {
                Cve = cve,
                Month = month,
                Title = Truncate(vulnerability.Title?.Value ?? cve, 512),
                Severity = serverThreats.Where(t => t.Type == SeverityThreat).Select(t => t.Description?.Value).Where(s => s is not null).MaxBy(s => Array.IndexOf(SeverityOrder, s)),
                Impact = serverThreats.FirstOrDefault(t => t.Type == ImpactThreat)?.Description?.Value is { } impact ? Truncate(impact, 64) : null,
                CvssScore = vulnerability.CVSSScoreSets.Where(s => s.ProductID.Any(builds.ContainsKey)).Select(s => (double?)s.BaseScore).Max(),
                Exploited = exploit.Contains("Exploited:Yes", StringComparison.OrdinalIgnoreCase),
                PubliclyDisclosed = exploit.Contains("Publicly Disclosed:Yes", StringComparison.OrdinalIgnoreCase),
            });
        }

        // The baseline of a version is the highest build fixing anything this month; older builds in the document belong to revised CVEs of earlier months.
        var baselines = fixes.Values
            .GroupBy(f => f.Build)
            .Select(g => g.MaxBy(f => f.Revision)!)
            .Select(f => new SecurityBaselineEntity { Build = f.Build, Month = month, ReleasedAtUtc = released, Revision = f.Revision, Kb = f.Kb })
            .ToList();
        return new MsrcMonth(month, released, baselines, vulnerabilities, [.. fixes.Values]);
    }

    /// <summary>The build of a Windows Server version by its MSRC product name, or null for any other product.</summary>
    internal static int? ServerBuild(string? productName)
    {
        foreach (var entry in Lifecycle.SupportLifecycle.Entries.Where(e => e.Server))
        {
            if (string.Equals(productName, entry.Name, StringComparison.Ordinal) || string.Equals(productName, entry.Name + " (Server Core installation)", StringComparison.Ordinal))
            {
                return entry.Build;
            }
        }

        return null;
    }

    /// <summary>"10.0.20348.5761" as build 20348 and revision 5761.</summary>
    internal static (int Build, int Revision)? ParseBuild(string? fixedBuild)
    {
        var parts = fixedBuild?.Split('.');
        return parts is [_, _, var build, var revision]
            && int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            && int.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var r)
                ? (b, r)
                : null;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
