using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>A software version a server needs or has, as an entry of the update catalog.</summary>
internal readonly record struct SoftwareNeed(Guid ClientId, Guid UpdateId, int Revision, DateTime ReportedAtUtc);

/// <summary>
/// What servers need of the software catalog, derived from their software inventory (docs/04-central-server.md, 7c; no
/// table of its own): a server needs every known version of a package that is newer than the one it has installed, and
/// it "has" the version it reports. The release planner and the deployment state treat these like the missing and
/// installed Windows updates an agent reports. Nothing is needed while software management is switched off.
/// </summary>
internal static class SoftwareNeeds
{
    /// <summary>The needed and the installed software versions of the servers (all servers when <paramref name="clientIds"/> is null).</summary>
    public static async Task<(IReadOnlyList<SoftwareNeed> Needed, IReadOnlyList<SoftwareNeed> Installed)> LoadAsync(ServerDbContext db, IReadOnlyCollection<Guid>? clientIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!await SoftwareFeature.IsEnabledAsync(db, cancellationToken).ConfigureAwait(false))
        {
            return ([], []);
        }

        var programs = await db.ClientSoftware.AsNoTracking()
            .Where(s => s.PackageId != null && s.InstalledVersion != null && (clientIds == null || clientIds.Contains(s.ClientId)))
            .Select(s => new { s.ClientId, s.PackageId, s.InstalledVersion, s.ReportedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (programs.Count == 0)
        {
            return ([], []);
        }

        var packageIds = programs.Select(p => p.PackageId!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var versions = (await db.PackageVersions.AsNoTracking()
                .Where(v => packageIds.Contains(v.PackageId) && v.ScanState != InstallerScanState.Detected)
                .Select(v => new { v.PackageId, v.Version, v.UpdateId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(v => v.PackageId, StringComparer.OrdinalIgnoreCase);

        var needed = new List<SoftwareNeed>();
        var installed = new List<SoftwareNeed>();
        foreach (var program in programs)
        {
            foreach (var version in versions[program.PackageId!])
            {
                var order = VersionComparer.Instance.Compare(version.Version, program.InstalledVersion);
                if (order > 0)
                {
                    needed.Add(new SoftwareNeed(program.ClientId, version.UpdateId, SoftwarePackages.SoftwareRevision, program.ReportedAtUtc));
                }
                else if (order == 0)
                {
                    installed.Add(new SoftwareNeed(program.ClientId, version.UpdateId, SoftwarePackages.SoftwareRevision, program.ReportedAtUtc));
                }
            }
        }

        return (needed.Distinct().ToList(), installed.Distinct().ToList());
    }
}
