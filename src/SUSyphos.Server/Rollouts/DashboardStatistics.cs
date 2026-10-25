using System.Globalization;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>The statistics the overview chart can show (Marc, 2026-10-23).</summary>
public enum StatisticKind
{
    Compliance,
    Servers,
    NewUpdates,
    Installations,
    OpenUpdates,
    TimeToInstall,
    Restarts,
    Errors,
}

public enum ChartForm
{
    /// <summary>One line per series over time, gaps where a value is missing.</summary>
    Lines,

    /// <summary>One bar per period, the series stacked.</summary>
    StackedBars,

    /// <summary>Horizontal bars, largest first (not over time).</summary>
    Ranking,
}

/// <param name="Name">Legend text.</param>
/// <param name="Class">CSS class of the colour: a status (ok, pend, queue, warn, fail, none) or a categorical slot (s1 … s8).</param>
/// <param name="Values">One value per label; null leaves a gap.</param>
public sealed record ChartSeries(string Name, string Class, IReadOnlyList<double?> Values);

/// <param name="Kind">Which statistic.</param>
/// <param name="Form">How it is drawn.</param>
/// <param name="Labels">Periods (days or weeks) or, for a ranking, the ranked items.</param>
/// <param name="Series">The values, one series per colour.</param>
/// <param name="Unit">Unit of the values, e.g. "%" or "days"; empty for counts.</param>
/// <param name="Max">Fixed top of the axis (100 for percent); null scales to the data.</param>
/// <param name="Note">What the chart shows and where the data comes from.</param>
public sealed record ChartData(StatisticKind Kind, ChartForm Form, IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries> Series, string Unit, double? Max, string Note)
{
    public bool IsEmpty => Series.Count == 0 || Series.All(s => s.Values.All(v => v is null or 0));
}

/// <summary>
/// Builds the data of the overview chart from what the central server keeps: the daily snapshots, the update catalog,
/// installations, the Windows Update history and the activity list. Up to 90 days per day, longer periods per week.
/// </summary>
internal sealed class DashboardStatistics(ServerDbContext db, ComplianceHistory history, TimeProvider timeProvider)
{
    public static readonly IReadOnlyList<int> Periods = [30, 90, 365];

    public static readonly IReadOnlyList<(StatisticKind Kind, string Key, string Label)> Kinds =
    [
        (StatisticKind.Compliance, "compliance", "Patch compliance and fully patched"),
        (StatisticKind.Servers, "servers", "Servers by status"),
        (StatisticKind.NewUpdates, "new-updates", "New updates by classification"),
        (StatisticKind.Installations, "installations", "Installations"),
        (StatisticKind.OpenUpdates, "open-updates", "Open updates: released and held back"),
        (StatisticKind.TimeToInstall, "time-to-install", "Time from release to installation"),
        (StatisticKind.Restarts, "restarts", "Restarts"),
        (StatisticKind.Errors, "errors", "Most frequent error codes"),
    ];

    /// <summary>Classifications in a fixed colour order, so a classification keeps its colour whatever else is shown.</summary>
    private static readonly string[] Classifications = ["Security Updates", "Critical Updates", "Definition Updates", "Update Rollups", "Updates", "Drivers", "Feature Packs"];

    public static StatisticKind ParseKind(string? key) => Kinds.FirstOrDefault(k => k.Key == key) is { Key: not null } found ? found.Kind : StatisticKind.Compliance;

    public static int ParsePeriod(string? days) => int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && Periods.Contains(value) ? value : 90;

    public async Task<ChartData> GetAsync(StatisticKind kind, int days, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var buckets = Buckets(today, days);
        return kind switch
        {
            StatisticKind.Servers => await ServersAsync(buckets, days, cancellationToken).ConfigureAwait(false),
            StatisticKind.NewUpdates => await NewUpdatesAsync(buckets, cancellationToken).ConfigureAwait(false),
            StatisticKind.Installations => await InstallationsAsync(buckets, cancellationToken).ConfigureAwait(false),
            StatisticKind.OpenUpdates => await OpenUpdatesAsync(buckets, days, cancellationToken).ConfigureAwait(false),
            StatisticKind.TimeToInstall => await TimeToInstallAsync(buckets, cancellationToken).ConfigureAwait(false),
            StatisticKind.Restarts => await RestartsAsync(buckets, cancellationToken).ConfigureAwait(false),
            StatisticKind.Errors => await ErrorsAsync(buckets[0].From, cancellationToken).ConfigureAwait(false),
            _ => await ComplianceAsync(buckets, days, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Periods of the chart, oldest first: days up to 90 days, weeks beyond.</summary>
    internal static List<(DateOnly From, DateOnly To)> Buckets(DateOnly today, int days)
    {
        var step = days > 90 ? 7 : 1;
        var count = (days + step - 1) / step;
        return [.. Enumerable.Range(0, count).Reverse().Select(i => (today.AddDays(-(i * step) - step + 1), today.AddDays(-(i * step))))];
    }

    private static string Label(DateOnly day) => day.ToString("d MMM", CultureInfo.InvariantCulture);

    private static List<string> Labels(List<(DateOnly From, DateOnly To)> buckets) => [.. buckets.Select(b => Label(b.From))];

    private static int Index(List<(DateOnly From, DateOnly To)> buckets, DateTime atUtc)
    {
        var day = DateOnly.FromDateTime(atUtc);
        return buckets.FindIndex(b => day >= b.From && day <= b.To);
    }

    /// <summary>The last snapshot within each period.</summary>
    private async Task<List<ComplianceSnapshotEntity?>> SnapshotsAsync(List<(DateOnly From, DateOnly To)> buckets, int days, CancellationToken cancellationToken)
    {
        var snapshots = await history.GetAsync(days + 7, cancellationToken).ConfigureAwait(false);

        // Today as it is now, so the chart agrees with the tiles (the stored record may be 15 minutes old).
        var live = await history.CaptureAsync(cancellationToken).ConfigureAwait(false);
        snapshots = [.. snapshots.Where(s => s.Day != live.Day), live];
        return [.. buckets.Select(b => snapshots.LastOrDefault(s => s.Day >= b.From && s.Day <= b.To))];
    }

    private async Task<ChartData> ComplianceAsync(List<(DateOnly From, DateOnly To)> buckets, int days, CancellationToken cancellationToken)
    {
        var snapshots = await SnapshotsAsync(buckets, days, cancellationToken).ConfigureAwait(false);
        static double? Percent(int? part, int servers) => part is { } value ? servers == 0 ? 100 : Math.Round(100.0 * value / servers, 1) : null;
        return new ChartData(
            StatisticKind.Compliance,
            ChartForm.Lines,
            Labels(buckets),
            [
                new ChartSeries("Patch compliance", "s1", [.. snapshots.Select(s => s is null ? (double?)null : Percent(s.Compliant, s.Servers))]),
                new ChartSeries("Fully patched", "s2", [.. snapshots.Select(s => s is null ? null : Percent(s.FullyPatched, s.Servers))]),
            ],
            "%",
            100,
            "Patch compliance: share of approved servers with every released update installed. Fully patched: share whose update search reports no missing update at all, released or not (recorded from version 0.7.7 on).");
    }

    private async Task<ChartData> ServersAsync(List<(DateOnly From, DateOnly To)> buckets, int days, CancellationToken cancellationToken)
    {
        var snapshots = await SnapshotsAsync(buckets, days, cancellationToken).ConfigureAwait(false);
        ChartSeries Series(string name, string cls, Func<ComplianceSnapshotEntity, int?> value) => new(name, cls, [.. snapshots.Select(s => s is null ? null : (double?)value(s))]);
        return new ChartData(
            StatisticKind.Servers,
            ChartForm.StackedBars,
            Labels(buckets),
            [
                Series("OK", "ok", s => s.StatusOk),
                Series("Updating", "pend", s => s.StatusUpdating),
                Series("Reboot pending", "queue", s => s.StatusRebootPending),
                Series("Warning", "warn", s => s.StatusWarning),
                Series("Failed", "fail", s => s.StatusFailed),
                Series("Offline", "none", s => s.StatusOffline),
            ],
            string.Empty,
            null,
            "Approved servers by status at the end of each day, as in the server list (recorded from version 0.7.7 on).");
    }

    private async Task<ChartData> NewUpdatesAsync(List<(DateOnly From, DateOnly To)> buckets, CancellationToken cancellationToken)
    {
        var from = buckets[0].From.ToDateTime(TimeOnly.MinValue);
        var updates = await db.Updates.AsNoTracking().Where(u => u.FirstSeenAtUtc >= from).Select(u => new { u.FirstSeenAtUtc, u.Classification }).ToListAsync(cancellationToken).ConfigureAwait(false);
        string Group(string? classification) => Classifications.FirstOrDefault(c => string.Equals(c, classification, StringComparison.OrdinalIgnoreCase)) ?? "Other";
        var groups = Classifications.Append("Other").ToList();
        var series = groups.Select((name, slot) =>
        {
            var values = new double?[buckets.Count];
            foreach (var update in updates.Where(u => Group(u.Classification) == name))
            {
                var index = Index(buckets, update.FirstSeenAtUtc);
                if (index >= 0)
                {
                    values[index] = (values[index] ?? 0) + 1;
                }
            }

            return new ChartSeries(name, "s" + (slot + 1).ToString(CultureInfo.InvariantCulture), values);
        }).Where(s => s.Values.Any(v => v > 0)).ToList();
        return new ChartData(StatisticKind.NewUpdates, ChartForm.StackedBars, Labels(buckets), series, string.Empty, null, "Updates the servers reported for the first time, by classification.");
    }

    private async Task<ChartData> InstallationsAsync(List<(DateOnly From, DateOnly To)> buckets, CancellationToken cancellationToken)
    {
        var from = buckets[0].From.ToDateTime(TimeOnly.MinValue);
        var installations = await db.Installations.AsNoTracking().Where(i => i.FinishedAtUtc >= from).Select(i => new { i.FinishedAtUtc, i.ResultCode, i.HResult }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var outside = await db.ClientUpdateHistory.AsNoTracking()
            .Where(h => h.DateUtc >= from && h.Operation == UpdateOperation.Installation && h.ClientApplicationId != UpdateHistoryEntry.AgentClientApplicationId
                && (h.ResultCode == InstallResultCode.Succeeded || h.ResultCode == InstallResultCode.SucceededWithErrors))
            .Select(h => h.DateUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var succeeded = new double?[buckets.Count];
        var failed = new double?[buckets.Count];
        var external = new double?[buckets.Count];
        foreach (var installation in installations)
        {
            var index = Index(buckets, installation.FinishedAtUtc);
            if (index >= 0)
            {
                var target = InstallEvidence.LeavesInstalled(installation.ResultCode, installation.HResult) ? succeeded : failed;
                target[index] = (target[index] ?? 0) + 1;
            }
        }

        foreach (var at in outside)
        {
            var index = Index(buckets, at);
            if (index >= 0)
            {
                external[index] = (external[index] ?? 0) + 1;
            }
        }

        return new ChartData(
            StatisticKind.Installations,
            ChartForm.StackedBars,
            Labels(buckets),
            [new ChartSeries("Succeeded", "ok", succeeded), new ChartSeries("Failed", "fail", failed), new ChartSeries("Outside SUSyphos", "queue", external)],
            string.Empty,
            null,
            "Installations by SUSyphos (succeeded, failed) and installations outside SUSyphos from the Windows Update history of the servers (kept for 90 days).");
    }

    private async Task<ChartData> OpenUpdatesAsync(List<(DateOnly From, DateOnly To)> buckets, int days, CancellationToken cancellationToken)
    {
        var snapshots = await SnapshotsAsync(buckets, days, cancellationToken).ConfigureAwait(false);
        return new ChartData(
            StatisticKind.OpenUpdates,
            ChartForm.StackedBars,
            Labels(buckets),
            [
                new ChartSeries("Released, not installed yet", "pend", [.. snapshots.Select(s => (double?)s?.OpenReleased)]),
                new ChartSeries("Held back by the rings", "queue", [.. snapshots.Select(s => (double?)s?.OpenHeld)]),
            ],
            string.Empty,
            null,
            "Server × update pairs still open at the end of each day: released and waiting for installation, or reported missing but not released to the server's ring yet (recorded from version 0.7.7 on).");
    }

    private async Task<ChartData> TimeToInstallAsync(List<(DateOnly From, DateOnly To)> buckets, CancellationToken cancellationToken)
    {
        var from = buckets[0].From.ToDateTime(TimeOnly.MinValue);
        var rings = await db.Rings.AsNoTracking().OrderBy(r => r.Order).ToListAsync(cancellationToken).ConfigureAwait(false);
        var installs = await db.Installations.AsNoTracking()
            .Where(i => i.FinishedAtUtc >= from && (i.ResultCode == (int)InstallResultCode.Succeeded || i.ResultCode == (int)InstallResultCode.SucceededWithErrors))
            .Join(db.Clients, i => i.ClientId, c => c.Id, (i, c) => new { i.UpdateId, i.Revision, i.FinishedAtUtc, c.RingId })
            .Join(db.RolloutRings.Where(r => r.ReleasedAtUtc != null), x => new { x.UpdateId, x.Revision, RingId = x.RingId ?? 0 }, r => new { r.UpdateId, r.Revision, r.RingId }, (x, r) => new { x.FinishedAtUtc, r.RingId, Released = r.ReleasedAtUtc!.Value })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var series = rings.Select((ring, slot) =>
        {
            var values = buckets.Select(b =>
            {
                var durations = installs
                    .Where(i => i.RingId == ring.Id && DateOnly.FromDateTime(i.FinishedAtUtc) >= b.From && DateOnly.FromDateTime(i.FinishedAtUtc) <= b.To)
                    .Select(i => Math.Max(0, (i.FinishedAtUtc - i.Released).TotalDays))
                    .Order()
                    .ToList();
                return durations.Count == 0 ? (double?)null : Math.Round(Median(durations), 1);
            }).ToList();
            return new ChartSeries(ring.Name, "s" + Math.Min(slot + 1, 8).ToString(CultureInfo.InvariantCulture), values);
        }).Where(s => s.Values.Any(v => v is not null)).ToList();
        return new ChartData(StatisticKind.TimeToInstall, ChartForm.Lines, Labels(buckets), series, "days", null, "Median days from the release of an update in a ring to its installation by SUSyphos on a server of that ring, by the ring the server is in now.");
    }

    private static double Median(List<double> sorted) => sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

    private async Task<ChartData> RestartsAsync(List<(DateOnly From, DateOnly To)> buckets, CancellationToken cancellationToken)
    {
        var from = buckets[0].From.ToDateTime(TimeOnly.MinValue);
        var restarts = await db.ClientActivities.AsNoTracking().Where(a => a.Kind == ClientActivityKind.Restarted && a.AtUtc >= from).Select(a => new { a.ClientId, a.AtUtc }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var reasons = await db.Installations.AsNoTracking().Where(i => i.RebootRequired && i.FinishedAtUtc >= from.AddDays(-1)).Select(i => new { i.ClientId, At = i.FinishedAtUtc }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var restartKinds = new[] { AgentActionKind.RestartServer, AgentActionKind.RestartServerInWindow, AgentActionKind.InstallNow, AgentActionKind.InstallNowRestartInWindow, AgentActionKind.Uninstall, AgentActionKind.UninstallRestartInWindow };
        var requested = await db.AgentActions.AsNoTracking().Where(a => restartKinds.Contains(a.Kind) && a.RequestedAtUtc >= from.AddDays(-1)).Select(a => new { a.ClientId, At = a.RequestedAtUtc }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var bySusyphos = new double?[buckets.Count];
        var other = new double?[buckets.Count];
        foreach (var restart in restarts)
        {
            var index = Index(buckets, restart.AtUtc);
            if (index < 0)
            {
                continue;
            }

            // A restart within a day after an installation that needed one, or after a requested restart, counts as SUSyphos'.
            var ours = reasons.Any(r => r.ClientId == restart.ClientId && r.At <= restart.AtUtc && restart.AtUtc - r.At <= TimeSpan.FromDays(1))
                || requested.Any(r => r.ClientId == restart.ClientId && r.At <= restart.AtUtc && restart.AtUtc - r.At <= TimeSpan.FromDays(1));
            var target = ours ? bySusyphos : other;
            target[index] = (target[index] ?? 0) + 1;
        }

        return new ChartData(
            StatisticKind.Restarts,
            ChartForm.StackedBars,
            Labels(buckets),
            [new ChartSeries("After SUSyphos installations or requests", "s1", bySusyphos), new ChartSeries("Other restarts", "none", other)],
            string.Empty,
            null,
            "Server starts the agents reported (the activity list keeps 90 days). A restart within a day after a SUSyphos installation that needed one, or after a requested restart, counts as SUSyphos'.");
    }

    private async Task<ChartData> ErrorsAsync(DateOnly fromDay, CancellationToken cancellationToken)
    {
        var from = fromDay.ToDateTime(TimeOnly.MinValue);
        var codes = (await db.Installations.AsNoTracking().Where(i => i.FinishedAtUtc >= from && i.ResultCode == (int)InstallResultCode.Failed).Select(i => i.HResult).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Concat(await db.ClientUpdateHistory.AsNoTracking().Where(h => h.DateUtc >= from && h.ResultCode == InstallResultCode.Failed).Select(h => h.HResult).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(code => code != InstallEvidence.AlreadyInstalled)
            .GroupBy(code => code)
            .Select(g => (Code: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .Take(10)
            .ToList();
        return new ChartData(
            StatisticKind.Errors,
            ChartForm.Ranking,
            [.. codes.Select(c => string.Create(CultureInfo.InvariantCulture, $"0x{c.Code:X8}: {HResultCatalog.Describe(c.Code)}"))],
            [new ChartSeries("Failed installations", "fail", [.. codes.Select(c => (double?)c.Count)])],
            string.Empty,
            null,
            "Error codes of failed installations in the period, by SUSyphos and outside SUSyphos (Windows Update history), the ten most frequent.");
    }
}
