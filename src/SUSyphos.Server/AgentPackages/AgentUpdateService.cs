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
        return TargetFor(packages, ringOrder, current, Minimum);
    }

    /// <summary>
    /// The package an agent of <paramref name="current"/> in the ring of <paramref name="ringOrder"/> should update to, or
    /// null if it runs that version or a newer one. <paramref name="packages"/> are the released and bundled packages.
    /// </summary>
    public static AgentPackageEntity? TargetFor(IReadOnlyCollection<AgentPackageEntity> packages, int ringOrder, AgentVersion current, AgentVersion minimum)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var target = AgentPackageAdministration.TargetFor(packages, ringOrder);
        if (current < minimum)
        {
            // Below the minimum the shipped package counts as released: it came with the central server itself.
            var candidates = packages.Where(p => p.Source == AgentPackageSource.Bundled || p.ReleasedThroughRingOrder >= ringOrder);
            target = AgentPackageStore.Newest(candidates.Where(p => AgentVersion.TryParse(p.Version, out var v) && v >= minimum)) ?? target;
        }

        return target is not null && AgentVersion.TryParse(target.Version, out var version) && version > current ? target : null;
    }

    /// <summary>Released and bundled packages, for <see cref="TargetFor(IReadOnlyCollection{AgentPackageEntity}, int, AgentVersion, AgentVersion)"/>.</summary>
    public Task<List<AgentPackageEntity>> GetOfferablePackagesAsync(CancellationToken cancellationToken) =>
        db.AgentPackages.AsNoTracking()
            .Where(p => p.ReleasedThroughRingOrder != null || p.Source == AgentPackageSource.Bundled)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Agent state and update target of each server, as the server list and the Agents page show them; servers without a
    /// ring get only packages released for all rings.
    /// </summary>
    public async Task<Dictionary<Guid, (Administration.AgentState State, string? Target)>> StatesAsync(IReadOnlyCollection<ClientEntity> clients, IReadOnlyDictionary<int, int> ringOrders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(ringOrders);
        var packages = await GetOfferablePackagesAsync(cancellationToken).ConfigureAwait(false);
        return clients.ToDictionary(c => c.Id, c =>
        {
            var known = AgentVersion.TryParse(c.AgentVersion, out var version);
            var order = c.RingId is { } ring && ringOrders.TryGetValue(ring, out var o) ? o : AgentPackageEntity.AllRings;
            var target = known && packages.Count > 0 ? TargetFor(packages, order, version!, Minimum)?.Version : null;
            return (Administration.ServerIndicators.Agent(c.AgentUpdateError, known, target), target);
        });
    }

    public async Task<AgentUpdate?> GetUpdateAsync(Guid clientId, string agentVersion, CancellationToken cancellationToken) =>
        await GetTargetAsync(clientId, agentVersion, cancellationToken).ConfigureAwait(false) is { } target
            ? new AgentUpdate(target.Version, target.Sha256, target.Size)
            : null;
}
