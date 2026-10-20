using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.AgentPackages;

/// <summary>
/// Tells an approved agent which agent version to update to: the newest package released for its ring that is newer than
/// the version it runs (docs/04-central-server.md, agent updates). Never a downgrade.
/// </summary>
internal sealed class AgentUpdateService(ServerDbContext db)
{
    public async Task<AgentPackageEntity?> GetTargetAsync(Guid clientId, string agentVersion, CancellationToken cancellationToken)
    {
        if (!AgentVersion.TryParse(agentVersion, out var current))
        {
            return null;
        }

        var released = await db.AgentPackages.AsNoTracking().Where(p => p.ReleasedThroughRingOrder != null).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (released.Count == 0)
        {
            return null;
        }

        // A server without a ring (not assigned yet) gets only packages released for all rings.
        var ringOrder = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Join(db.Rings, c => c.RingId, r => r.Id, (c, r) => (int?)r.Order)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? AgentPackageEntity.AllRings;
        var target = AgentPackageAdministration.TargetFor(released, ringOrder);
        return target is not null && AgentVersion.TryParse(target.Version, out var version) && version > current ? target : null;
    }

    public async Task<AgentUpdate?> GetUpdateAsync(Guid clientId, string agentVersion, CancellationToken cancellationToken) =>
        await GetTargetAsync(clientId, agentVersion, cancellationToken).ConfigureAwait(false) is { } target
            ? new AgentUpdate(target.Version, target.Sha256, target.Size)
            : null;
}
