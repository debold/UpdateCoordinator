using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>A package of the catalog as the Software page lists it.</summary>
/// <param name="Package">The catalog entry.</param>
/// <param name="Servers">Approved servers it is installed on.</param>
/// <param name="InstalledVersions">Installed versions, newest first.</param>
/// <param name="Newest">Newest known version; null while none is known.</param>
/// <param name="Rollout">State of the newest version: label and pill class.</param>
internal sealed record SoftwareRow(PackageEntity Package, int Servers, IReadOnlyList<string> InstalledVersions, PackageVersionEntity? Newest, (string Label, string Class) Rollout)
{
    /// <summary>Servers that run an older version than the newest one.</summary>
    public int Outdated { get; init; }
}

/// <summary>Reads the catalog for the Software page and the package page (one query path for both).</summary>
internal sealed class SoftwareOverview(ServerDbContext db)
{
    public async Task<IReadOnlyList<SoftwareRow>> ListAsync(CancellationToken cancellationToken)
    {
        var packages = await db.Packages.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var versions = (await db.PackageVersions.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(v => v.PackageId, StringComparer.OrdinalIgnoreCase);
        var approved = db.Clients.Where(c => c.State == SUSyphos.Contracts.Agents.RegistrationState.Approved).Select(c => c.Id);
        var installed = (await db.ClientSoftware.AsNoTracking()
                .Where(s => s.PackageId != null && approved.Contains(s.ClientId))
                .Select(s => new { s.PackageId, s.ClientId, s.InstalledVersion })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(s => s.PackageId!, StringComparer.OrdinalIgnoreCase);
        var rollouts = (await db.Rollouts.AsNoTracking().Where(r => db.PackageVersions.Any(v => v.UpdateId == r.UpdateId)).Select(r => new { r.UpdateId, r.State }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.UpdateId, r => r.State);

        return [.. packages.Select(package =>
        {
            var newest = versions[package.PackageId].OrderByDescending(v => v.Version, VersionComparer.Instance).FirstOrDefault();
            var onServers = installed[package.PackageId].ToList();
            var outdated = newest is null ? 0 : onServers.Where(s => s.InstalledVersion is not null && VersionComparer.Instance.Compare(s.InstalledVersion, newest.Version) < 0).Select(s => s.ClientId).Distinct().Count();
            var newer = newest is null ? 0 : onServers.Where(s => s.InstalledVersion is not null && VersionComparer.Instance.Compare(s.InstalledVersion, newest.Version) > 0).Select(s => s.ClientId).Distinct().Count();
            var servers = onServers.Select(s => s.ClientId).Distinct().Count();
            var state = RolloutState(package, newest, newest is not null && rollouts.TryGetValue(newest.UpdateId, out var rollout) ? rollout : null);

            // Nobody runs an older version: nothing to roll out (Marc, 2026-11-13), whatever the rollout of the newest says.
            if (servers > 0 && outdated == 0 && state.Class is "neutral" or "pend")
            {
                state = newer == servers ? ("Newer than the source", "neutral") : ("Up to date", "ok");
            }

            return new SoftwareRow(package, servers, Distinct(onServers.Select(s => s.InstalledVersion).OfType<string>()), newest, state)
            {
                Outdated = outdated,
            };
        })];
    }

    /// <summary>
    /// Versions without repeats, newest first: "26.06" and "26.06.00.0" are the same version and shown once, in the shorter
    /// spelling (Marc, 2026-11-13).
    /// </summary>
    internal static IReadOnlyList<string> Distinct(IEnumerable<string> versions)
    {
        var result = new List<string>();
        foreach (var version in versions.OrderBy(v => v.Length))
        {
            if (!result.Any(known => VersionComparer.Instance.Compare(known, version) == 0))
            {
                result.Add(version);
            }
        }

        return [.. result.OrderByDescending(v => v, VersionComparer.Instance)];
    }

    /// <summary>The state of the newest version as a pill: no version, blocked by the virus scan, waiting for the agreements, queued, rolling out or stopped.</summary>
    internal static (string Label, string Class) RolloutState(PackageEntity package, PackageVersionEntity? newest, Data.RolloutState? rollout) => newest switch
    {
        null => ("No version", "neutral"),
        { ScanState: InstallerScanState.Detected } => ("Threat found", "fail"),
        _ when rollout == Data.RolloutState.Active => ("Rolling out", "pend"),
        _ when rollout == Data.RolloutState.Stopped => ("Stopped", "fail"),
        _ when package.HasAgreements && package.AgreementsAcceptedAtUtc is null => ("Agreements not accepted", "queue"),
        _ => ("Queued", "neutral"),
    };

    /// <summary>Text of a scan state for the dashboard.</summary>
    internal static (string Label, string Class) ScanLabel(InstallerScanState state) => state switch
    {
        InstallerScanState.Clean => ("Clean", "ok"),
        InstallerScanState.Detected => ("Threat found", "fail"),
        InstallerScanState.Failed => ("Not scanned (failed)", "queue"),
        _ => ("Not scanned", "neutral"),
    };
}
