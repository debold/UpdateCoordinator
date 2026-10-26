using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.Server.Lifecycle;

namespace SUSyphos.Server.Security;

/// <summary>One Windows version on the page "Operating systems".</summary>
/// <param name="Build">Build of the version.</param>
/// <param name="Name">Its name from the lifecycle table.</param>
/// <param name="Servers">Approved servers of the version.</param>
/// <param name="Current">Of those, servers on the security baseline.</param>
/// <param name="Baseline">The newest baseline of the version.</param>
/// <param name="Open">Vulnerabilities open on at least one server of the version.</param>
/// <param name="MedianDays">Median days from the release of a baseline to its installation; null without installations.</param>
/// <param name="Support">Support of the version itself (without ESU of single servers).</param>
internal sealed record VersionRow(int Build, string Name, int Servers, int Current, SecurityBaselineEntity? Baseline, IReadOnlyList<SecurityVulnerabilityEntity> Open, double? MedianDays, SupportInfo Support)
{
    public int Critical => Open.Count(v => v.Severity == "Critical");

    public int Exploited => Open.Count(v => v.Exploited);

    public int? Percent => Components.DisplayNumber.Share(Current, Servers);
}

/// <summary>A vulnerability open on approved servers, with how many and the update that fixes it.</summary>
internal sealed record OpenVulnerability(SecurityVulnerabilityEntity Vulnerability, int Servers, string? Kb);

/// <summary>The figures of the page "Operating systems", the overview tile and the statistics, computed in one place.</summary>
internal static class SecurityReport
{
    /// <summary>The Windows versions of the approved servers, Windows Server first, newest first.</summary>
    public static List<VersionRow> Versions(IReadOnlyCollection<ClientEntity> clients, SecuritySnapshot snapshot, SupportLifecycle lifecycle, IReadOnlyDictionary<int, double> medians)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(lifecycle);
        return [.. clients
            .Select(c => (Client: c, Entry: SupportLifecycle.Find(c.OsVersion, c.OsEditionId, c.OsProductName), Security: snapshot.Of(c)))
            .Where(c => c.Entry is not null)
            .GroupBy(c => c.Entry!)
            .Select(g =>
            {
                var first = g.First();
                var open = g.SelectMany(c => c.Security.Open).DistinctBy(v => v.Cve).OrderByDescending(v => v.Exploited).ThenByDescending(v => v.CvssScore ?? 0).ToList();
                return new VersionRow(
                    g.Key.Build,
                    g.Key.Name,
                    g.Count(),
                    g.Count(c => c.Security.State == BaselineState.Current),
                    snapshot.Baselines(g.Key.Build) is [var newest, ..] ? newest : null,
                    open,
                    medians.TryGetValue(g.Key.Build, out var median) ? median : null,
                    lifecycle.Of(first.Client.OsVersion, first.Client.OsEditionId, first.Client.OsProductName));
            })
            .OrderByDescending(r => r.Name.StartsWith("Windows Server", StringComparison.Ordinal))
            .ThenByDescending(r => r.Build)];
    }

    /// <summary>Vulnerabilities open on approved servers: exploited first, then by CVSS score.</summary>
    public static List<OpenVulnerability> Open(IReadOnlyCollection<ClientEntity> clients, SecuritySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var perServer = clients.Select(c => (Build: Agents.OperatingSystems.BuildNumber(c.OsVersion), Security: snapshot.Of(c))).ToList();
        return [.. perServer
            .SelectMany(s => s.Security.Open.Select(v => (s.Build, Vulnerability: v)))
            .GroupBy(x => x.Vulnerability.Cve)
            .Select(g => new OpenVulnerability(
                g.First().Vulnerability,
                g.Count(),
                snapshot.FixesOf(g.Key).Where(f => g.Any(x => x.Build == f.Build)).OrderByDescending(f => f.Revision).Select(f => f.Kb).FirstOrDefault(k => k is not null)))
            .OrderByDescending(o => o.Vulnerability.Exploited)
            .ThenByDescending(o => o.Vulnerability.CvssScore ?? 0)
            .ThenByDescending(o => o.Servers)];
    }

    /// <summary>
    /// Median days from the release of a baseline (patch day) to its installation, per Windows version: installations by
    /// SUSyphos of the baseline's KB and successful installations of it in the Windows Update history.
    /// </summary>
    public static async Task<Dictionary<int, double>> MedianDaysAsync(ServerDbContext db, SecuritySnapshot snapshot, IReadOnlyCollection<ClientEntity> clients, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clients);
        var builds = clients.ToDictionary(c => c.Id, c => Agents.OperatingSystems.BuildNumber(c.OsVersion));
        var baselines = clients.Select(c => builds[c.Id]).OfType<int>().Distinct().SelectMany(snapshot.Baselines).Where(b => b.Kb is not null).ToList();
        if (baselines.Count == 0)
        {
            return [];
        }

        var kbs = baselines.Select(b => b.Kb!).Distinct().ToList();
        var since = baselines.Min(b => b.ReleasedAtUtc);
        var ids = clients.Select(c => c.Id).ToList();
        var installed = await (
                from installation in db.Installations.AsNoTracking()
                join update in db.Updates.AsNoTracking() on new { installation.UpdateId, installation.Revision } equals new { update.UpdateId, update.Revision }
                where ids.Contains(installation.ClientId) && installation.FinishedAtUtc >= since && update.KbArticle != null && kbs.Contains(update.KbArticle)
                    && (installation.ResultCode == (int)InstallResultCode.Succeeded || installation.ResultCode == (int)InstallResultCode.SucceededWithErrors)
                select new { installation.ClientId, Kb = update.KbArticle!, installation.FinishedAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var history = (await db.ClientUpdateHistory.AsNoTracking()
                .Where(h => ids.Contains(h.ClientId) && h.DateUtc >= since && h.Operation == UpdateOperation.Installation
                    && (h.ResultCode == InstallResultCode.Succeeded || h.ResultCode == InstallResultCode.SucceededWithErrors))
                .Select(h => new { h.ClientId, h.Title, h.DateUtc })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(h => new { h.ClientId, Kb = Rollouts.UpdateText.KbIn(h.Title), FinishedAtUtc = h.DateUtc })
            .Where(h => h.Kb is not null && kbs.Contains(h.Kb));

        // One value per server and baseline: the first installation.
        return installed.Concat(history.Select(h => new { h.ClientId, Kb = h.Kb!, h.FinishedAtUtc }))
            .Select(i => (i.ClientId, Build: builds.GetValueOrDefault(i.ClientId), Baseline: baselines.FirstOrDefault(b => b.Kb == i.Kb && b.Build == builds.GetValueOrDefault(i.ClientId)), i.FinishedAtUtc))
            .Where(i => i.Baseline is not null && i.FinishedAtUtc >= i.Baseline.ReleasedAtUtc)
            .GroupBy(i => (i.ClientId, i.Baseline!.Month))
            .Select(g => (Build: g.First().Build!.Value, Days: (g.Min(i => i.FinishedAtUtc) - g.First().Baseline!.ReleasedAtUtc).TotalDays))
            .GroupBy(i => i.Build)
            .ToDictionary(g => g.Key, g => Median([.. g.Select(i => i.Days)]));
    }

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
