using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.AgentPackages;

/// <summary>
/// Tells an approved agent which agent version to update to: the newest package released for its ring that is newer than
/// the version it runs (docs/04-central-server.md, agent updates). Never a downgrade. An agent below the minimum version
/// (<see cref="AgentPackageOptions.MinimumVersion"/>) also gets the package shipped with the central server, released or not,
/// because it may no longer understand the protocol.
/// </summary>
internal sealed class AgentUpdateService(ServerDbContext db, IOptions<AgentPackageOptions> options)
{
    /// <summary>First agent version that can update itself; older agents need the MSI once.</summary>
    public static readonly AgentVersion BuiltInMinimum = new(0, 4, 0, null);

    /// <summary>The effective minimum: the built-in one or a higher configured one.</summary>
    public AgentVersion Minimum =>
        AgentVersion.TryParse(options.Value.MinimumVersion, out var configured) && configured > BuiltInMinimum ? configured : BuiltInMinimum;

    public bool IsBelowMinimum(string? agentVersion) => AgentVersion.TryParse(agentVersion, out var version) && version < Minimum;

    public async Task<AgentPackageEntity?> GetTargetAsync(Guid clientId, string agentVersion, CancellationToken cancellationToken)
    {
        if (!AgentVersion.TryParse(agentVersion, out var current))
        {
            return null;
        }

        var packages = await db.AgentPackages.AsNoTracking()
            .Where(p => p.ReleasedThroughRingOrder != null || p.Source == AgentPackageSource.Bundled)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (packages.Count == 0)
        {
            return null;
        }

        // A server without a ring (not assigned yet) gets only packages released for all rings.
        var ringOrder = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Join(db.Rings, c => c.RingId, r => r.Id, (c, r) => (int?)r.Order)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? AgentPackageEntity.AllRings;
        var target = AgentPackageAdministration.TargetFor(packages, ringOrder);
        if (current < Minimum)
        {
            // Below the minimum the shipped package counts as released: it came with the central server itself.
            var candidates = packages.Where(p => p.Source == AgentPackageSource.Bundled || p.ReleasedThroughRingOrder >= ringOrder);
            target = AgentPackageStore.Newest(candidates.Where(p => AgentVersion.TryParse(p.Version, out var v) && v >= Minimum)) ?? target;
        }

        return target is not null && AgentVersion.TryParse(target.Version, out var version) && version > current ? target : null;
    }

    public async Task<AgentUpdate?> GetUpdateAsync(Guid clientId, string agentVersion, CancellationToken cancellationToken) =>
        await GetTargetAsync(clientId, agentVersion, cancellationToken).ConfigureAwait(false) is { } target
            ? new AgentUpdate(target.Version, target.Sha256, target.Size)
            : null;
}
