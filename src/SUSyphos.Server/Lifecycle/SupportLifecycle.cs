using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Lifecycle;

/// <summary>Thresholds for the end-of-support warning (configuration section <c>Lifecycle</c>).</summary>
internal sealed class LifecycleOptions
{
    public const string Section = "Lifecycle";

    /// <summary>From this many days before the end of support a server shows "Ending soon" (amber, default 180).</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 3650)]
    public int WarningDays { get; set; } = 180;
}

/// <summary>
/// The support phase of a server's Windows version (Marc, 2026-10-26): mainstream (Supported), extended (Security only),
/// close to the end (Ending soon), after the end with Extended Security Updates (Esu), after the end before the first
/// missed patch day without ESU found (Ended), and without ESU after that (EndOfSupport).
/// </summary>
internal enum SupportState
{
    Unknown,
    Supported,
    SecurityOnly,
    EndingSoon,
    Esu,
    Ended,
    EndOfSupport,
}

/// <summary>Where Extended Security Updates of a server come from.</summary>
internal enum EsuSource
{
    None,
    Key,
    Azure,
    Inferred,
}

/// <summary>The Windows version of a server with its support dates and state.</summary>
/// <param name="Name">Product name, e.g. "Windows Server 2016" or "Windows 11 Enterprise, version 24H2".</param>
/// <param name="MainstreamEnd">End of mainstream support where Microsoft names one.</param>
/// <param name="End">Last day of updates; null when the version is unknown.</param>
/// <param name="DaysLeft">Days until <paramref name="End"/>, negative after it.</param>
/// <param name="State">The support phase.</param>
/// <param name="Esu">Where ESU come from after the end; <see cref="EsuSource.None"/> otherwise.</param>
internal sealed record SupportInfo(string? Name, DateOnly? MainstreamEnd, DateOnly? End, int? DaysLeft, SupportState State, EsuSource Esu = EsuSource.None)
{
    public static readonly SupportInfo Unknown = new(null, null, null, null, SupportState.Unknown);

    /// <summary>The word for the state, with the ESU source: "ESU (key)", "ESU, inferred from updates".</summary>
    public string Label => SupportLifecycle.Label(State, Esu);
}

/// <summary>
/// End of support of the Windows versions (docs/04-central-server.md, lifecycle), from the table shipped with every
/// release (<c>Lifecycle/lifecycle.json</c>, Marc 2026-10-26). The version is recognised by build, server or client and
/// edition, as the agent reports them. One place for names and dates, so list, server page, overview and notifications agree.
/// </summary>
internal sealed class SupportLifecycle(IOptions<LifecycleOptions> options, TimeProvider timeProvider)
{
    // Before the table, because static initializers run in order and the table is read with these options.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The entries of the shipped table in their order.</summary>
    public static IReadOnlyList<LifecycleEntry> Entries { get; } = Load();

    /// <summary>When the shipped table was compiled.</summary>
    public static DateOnly AsOf { get; private set; }

    /// <summary>Expected release of the next Windows Server version; a test fails once it has passed, so a release reviews the table.</summary>
    public static DateOnly NextServerRelease { get; private set; }

    public LifecycleOptions Options => options.Value;

    /// <summary>Support of a server, as of today in the time zone of the central server; <paramref name="security"/> lets updates after the end count as ESU.</summary>
    public SupportInfo Of(ClientEntity client, Security.ServerSecurity? security = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Of(client.OsVersion, client.OsEditionId, client.OsProductName, Detected(client.EsuKey, client.Azure), security);
    }

    /// <summary>The ESU source the agent found: a licensed key first, then Azure. An Azure Arc agent alone is no proof of ESU (Marc, 2026-10-26); such a server counts as ESU only once it installs updates released after the end.</summary>
    public static EsuSource Detected(bool? key, bool? azure) =>
        key == true ? EsuSource.Key : azure == true ? EsuSource.Azure : EsuSource.None;

    public SupportInfo Of(string? osVersion, string? editionId, string? productName, EsuSource detected = EsuSource.None, Security.ServerSecurity? security = null)
    {
        if (Find(osVersion, editionId, productName) is not { } entry)
        {
            return SupportInfo.Unknown;
        }

        var today = DateOnly.FromDateTime(Components.DisplayTime.ToLocal(timeProvider.GetUtcNow().UtcDateTime));
        var days = entry.End.DayNumber - today.DayNumber;
        if (days > options.Value.WarningDays)
        {
            var mainstream = entry.Mainstream is not { } until || until >= today;
            return new SupportInfo(entry.Name, entry.Mainstream, entry.End, days, mainstream ? SupportState.Supported : SupportState.SecurityOnly);
        }

        if (days >= 0)
        {
            return new SupportInfo(entry.Name, entry.Mainstream, entry.End, days, SupportState.EndingSoon);
        }

        // After the end: ESU found by the agent, or the server keeps getting the security updates released since (ESU inferred).
        if (detected != EsuSource.None)
        {
            return new SupportInfo(entry.Name, entry.Mainstream, entry.End, days, SupportState.Esu, detected);
        }

        var endUtc = entry.End.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        if (security is { State: Security.BaselineState.Current, Target: { } target } && target.ReleasedAtUtc > endUtc)
        {
            return new SupportInfo(entry.Name, entry.Mainstream, entry.End, days, SupportState.Esu, EsuSource.Inferred);
        }

        // "No ESU" only once the first patch day after the end has passed without the server getting its updates.
        var firstPatchDay = DateOnly.FromDateTime(Security.MsrcSync.PatchDay(entry.End.AddMonths(1).Year, entry.End.AddMonths(1).Month));
        return new SupportInfo(entry.Name, entry.Mainstream, entry.End, days, today > firstPatchDay ? SupportState.EndOfSupport : SupportState.Ended);
    }

    /// <summary>The table entry of a Windows version, or null when the table does not know it.</summary>
    public static LifecycleEntry? Find(string? osVersion, string? editionId, string? productName)
    {
        if (Agents.OperatingSystems.BuildNumber(osVersion) is not { } build)
        {
            return null;
        }

        var server = IsServer(build, editionId, productName);
        return Entries.FirstOrDefault(e => e.Build == build && e.Server == server
            && (e.Editions is null || (editionId is not null && e.Editions.Contains(editionId, StringComparer.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// Windows Server or a client? The edition says it ("ServerStandard", "ServerDatacenter"), else the product name; without
    /// either, a build Windows Server shares with clients counts as server, since SUSyphos manages servers.
    /// </summary>
    private static bool IsServer(int build, string? editionId, string? productName) =>
        !string.IsNullOrEmpty(editionId) ? editionId.StartsWith("Server", StringComparison.OrdinalIgnoreCase) && !editionId.Equals("ServerRdsh", StringComparison.OrdinalIgnoreCase)
        : !string.IsNullOrWhiteSpace(productName) ? productName.Contains("Server", StringComparison.OrdinalIgnoreCase)
        : Entries.Any(e => e.Server && e.Build == build);

    public static string Label(SupportState state, EsuSource esu = EsuSource.None) => state switch
    {
        SupportState.Supported => "Supported",
        SupportState.SecurityOnly => "Security only",
        SupportState.EndingSoon => "Ending soon",
        SupportState.Esu => esu switch
        {
            EsuSource.Key => "ESU (key)",
            EsuSource.Azure => "ESU (Azure)",
            _ => "ESU, inferred from updates",
        },
        SupportState.Ended => "Support ended",
        SupportState.EndOfSupport => "No ESU",
        _ => "Unknown",
    };

    public static string Class(SupportState state) => state switch
    {
        SupportState.Supported or SupportState.SecurityOnly => "ok",
        SupportState.EndingSoon or SupportState.Esu or SupportState.Ended => "queue",
        SupportState.EndOfSupport => "fail",
        _ => "neutral",
    };

    /// <summary>States that belong under "Needs attention" and into a notification.</summary>
    public static bool NeedsAttention(SupportState state) => state is SupportState.EndingSoon or SupportState.Ended or SupportState.EndOfSupport;

    /// <summary>"ends 2027-01-12 (in 78 days)" or "ended 2025-10-14 (378 days ago)"; "–" when unknown.</summary>
    public static string Describe(SupportInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.End is not { } end || info.DaysLeft is not { } days)
        {
            return "–";
        }

        var date = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return days switch
        {
            < 0 => $"ended {date} ({Components.DisplayNumber.Format(-days)} days ago)",
            0 => $"ends {date} (today)",
            1 => $"ends {date} (tomorrow)",
            _ => $"ends {date} (in {Components.DisplayNumber.Format(days)} days)",
        };
    }

    private static List<LifecycleEntry> Load()
    {
        using var stream = typeof(SupportLifecycle).Assembly.GetManifestResourceStream("SUSyphos.Server.Lifecycle.lifecycle.json")
            ?? throw new InvalidOperationException("The lifecycle table is missing from the central server.");
        var table = JsonSerializer.Deserialize<LifecycleTable>(stream, JsonOptions) ?? throw new InvalidOperationException("The lifecycle table is empty.");
        AsOf = table.AsOf;
        NextServerRelease = table.NextServerRelease;
        return table.Entries;
    }

    private sealed record LifecycleTable(DateOnly AsOf, DateOnly NextServerRelease, List<LifecycleEntry> Entries);
}

/// <summary>One Windows version in the lifecycle table.</summary>
internal sealed record LifecycleEntry(
    string Name,
    int Build,
    bool Server,
    DateOnly End,
    DateOnly? Mainstream = null,
    [property: JsonPropertyName("editions")] IReadOnlyList<string>? Editions = null);
