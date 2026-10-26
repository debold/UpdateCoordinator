using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Security;

/// <summary>Thresholds of the security baseline under "Needs attention" (configuration section <c>Security</c>).</summary>
internal sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>
    /// A server missing a baseline released longer ago than this many days needs attention (absolute threshold); null
    /// takes the longest way through the rings: the maximum waits of all transitions plus a week for the maintenance windows.
    /// </summary>
    [Range(1, 365)]
    public int? BehindDays { get; set; }

    /// <summary>When at least this share of the servers is behind (in percent), the overview says so too (relative threshold).</summary>
    [Range(1, 100)]
    public int BehindPercent { get; set; } = 20;
}

/// <summary>How far a server is from the security baseline of its Windows version.</summary>
internal enum BaselineState
{
    Unknown,
    Current,
    OneBehind,
    TwoBehind,
}

/// <summary>The security state of one server.</summary>
/// <param name="State">Current, one or two (or more) monthly baselines behind, or unknown (no MSRC data, unknown version).</param>
/// <param name="Build">Build of the server's Windows version.</param>
/// <param name="Revision">Update revision the server runs.</param>
/// <param name="Target">The newest baseline of its version: the build that closes everything published.</param>
/// <param name="Missing">The oldest baseline the server misses; its release date is when the server fell behind.</param>
/// <param name="Open">Vulnerabilities fixed in a later build than the server runs.</param>
internal sealed record ServerSecurity(BaselineState State, int? Build, int? Revision, SecurityBaselineEntity? Target, SecurityBaselineEntity? Missing, IReadOnlyList<SecurityVulnerabilityEntity> Open)
{
    public static readonly ServerSecurity Unknown = new(BaselineState.Unknown, null, null, null, null, []);

    public int Critical => Open.Count(v => v.Severity == "Critical");

    public int Exploited => Open.Count(v => v.Exploited);
}

/// <summary>
/// The MSRC data of the central server loaded once (per page or check) and evaluated per server: baseline state and open
/// vulnerabilities from the build and update revision the agent reports (Marc, 2026-10-26).
/// </summary>
internal sealed class SecuritySnapshot
{
    private readonly ILookup<int, SecurityBaselineEntity> _baselines;
    private readonly ILookup<int, SecurityFixEntity> _fixes;
    private readonly Dictionary<string, SecurityVulnerabilityEntity> _vulnerabilities;

    public SecuritySnapshot(IEnumerable<SecurityBaselineEntity> baselines, IEnumerable<SecurityFixEntity> fixes, IEnumerable<SecurityVulnerabilityEntity> vulnerabilities)
    {
        _baselines = baselines.OrderByDescending(b => b.ReleasedAtUtc).ToLookup(b => b.Build);
        _fixes = fixes.ToLookup(f => f.Build);

        // A CVE revised in a later month is kept with its newest data.
        _vulnerabilities = vulnerabilities.GroupBy(v => v.Cve).ToDictionary(g => g.Key, g => g.MaxBy(v => MonthOrder(v.Month))!);
    }

    public static readonly SecuritySnapshot Empty = new([], [], []);

    /// <summary>True when there is MSRC data at all.</summary>
    public bool HasData => _vulnerabilities.Count > 0;

    /// <summary>The baselines of a Windows version, newest first.</summary>
    public IReadOnlyList<SecurityBaselineEntity> Baselines(int build) => [.. _baselines[build]];

    public IReadOnlyCollection<SecurityVulnerabilityEntity> Vulnerabilities => _vulnerabilities.Values;

    /// <summary>The Windows Server builds of the version and their fixing revision for one vulnerability.</summary>
    public IEnumerable<SecurityFixEntity> FixesOf(string cve) => _fixes.SelectMany(g => g).Where(f => f.Cve == cve);

    /// <summary>Vulnerabilities an update (by KB) fixes on Windows Server.</summary>
    public IReadOnlyList<SecurityVulnerabilityEntity> FixedBy(string? kb) =>
        kb is null ? [] : [.. _fixes.SelectMany(g => g).Where(f => f.Kb == kb).Select(f => f.Cve).Distinct().Select(c => _vulnerabilities.GetValueOrDefault(c)).OfType<SecurityVulnerabilityEntity>()];

    public ServerSecurity Of(ClientEntity client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Of(client.OsVersion);
    }

    public ServerSecurity Of(string? osVersion)
    {
        if (MsrcParser.ParseBuild(osVersion) is not { } version)
        {
            return ServerSecurity.Unknown;
        }

        var baselines = _baselines[version.Build].ToList();
        if (baselines.Count == 0)
        {
            return ServerSecurity.Unknown with { Build = version.Build, Revision = version.Revision };
        }

        var missed = baselines.Where(b => b.Revision > version.Revision).ToList();
        var state = missed.Count switch
        {
            0 => BaselineState.Current,
            1 => BaselineState.OneBehind,
            _ => BaselineState.TwoBehind,
        };
        var open = _fixes[version.Build]
            .Where(f => f.Revision > version.Revision)
            .Select(f => f.Cve)
            .Distinct()
            .Select(c => _vulnerabilities.GetValueOrDefault(c))
            .OfType<SecurityVulnerabilityEntity>()
            .OrderByDescending(v => v.Exploited).ThenByDescending(v => v.CvssScore ?? 0).ThenBy(v => v.Cve, StringComparer.Ordinal)
            .ToList();
        return new ServerSecurity(state, version.Build, version.Revision, baselines[0], missed.LastOrDefault(), open);
    }

    public static string Label(BaselineState state) => state switch
    {
        BaselineState.Current => "Current",
        BaselineState.OneBehind => "1 behind",
        BaselineState.TwoBehind => "2 or more behind",
        _ => "Unknown",
    };

    public static string Class(BaselineState state) => state switch
    {
        BaselineState.Current => "ok",
        BaselineState.OneBehind => "queue",
        BaselineState.TwoBehind => "fail",
        _ => "neutral",
    };

    /// <summary>Sort key of an MSRC document id ("2026-Oct"), later months larger.</summary>
    public static int MonthOrder(string month) =>
        DateTime.TryParseExact(month, "yyyy-MMM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date.Year * 12 + date.Month : 0;
}

/// <summary>Loads the MSRC data of the central server and the threshold for servers that are behind.</summary>
internal sealed class SecurityBaseline(ServerDbContext db, Microsoft.Extensions.Options.IOptions<SecurityOptions> options)
{
    public async Task<SecuritySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var baselines = await db.SecurityBaselines.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (baselines.Count == 0)
        {
            return SecuritySnapshot.Empty;
        }

        var fixes = await db.SecurityFixes.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var vulnerabilities = await db.SecurityVulnerabilities.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return new SecuritySnapshot(baselines, fixes, vulnerabilities);
    }

    /// <summary>
    /// The absolute threshold in days: configured, or the longest way through the rings (maximum waits of all transitions)
    /// plus a week for the maintenance windows.
    /// </summary>
    public async Task<int> BehindDaysAsync(CancellationToken cancellationToken)
    {
        if (options.Value.BehindDays is { } configured)
        {
            return configured;
        }

        var waits = await db.RingTransitions.AsNoTracking().Select(t => t.MaximumWait).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (int)Math.Ceiling(waits.Sum(w => w.TotalDays)) + 7;
    }

    public int BehindPercent => options.Value.BehindPercent;
}
