using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>
/// Derives the releases of clients from the rollout state: an approved client gets an update when the active rollout
/// released it for the client's ring and the client reported it as missing. A release stays until the update is
/// reported as installed, so late install reports are still accepted. A change bumps the release version.
/// An update superseded by another update the client gets or has installed is withdrawn for that client, so each
/// ring gets the newer update as soon as it is released there and keeps the older one until then.
/// </summary>
internal sealed class ReleasePlanner(ServerDbContext db, TimeProvider timeProvider)
{
    /// <summary>Synchronizes one client, or all clients if <paramref name="clientId"/> is null. Saves the changes.</summary>
    public async Task SyncAsync(Guid? clientId, CancellationToken cancellationToken)
    {
        var clients = await db.Clients
            .Where(c => clientId == null || c.Id == clientId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (clients.Count == 0)
        {
            return;
        }

        var released = (await db.RolloutRings.AsNoTracking()
                .Where(r => r.State == RingRolloutState.Released)
                .Join(db.Rollouts.Where(r => r.State == RolloutState.Active), r => new { r.UpdateId, r.Revision }, o => new { o.UpdateId, o.Revision }, (r, _) => r)
                .Select(r => new { r.UpdateId, r.Revision, r.RingId, r.ReleasedAtUtc })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(r => (r.UpdateId, r.Revision, r.RingId), r => r.ReleasedAtUtc ?? DateTime.MinValue);

        var releasedIds = released.Keys.Select(k => k.UpdateId).Distinct().ToList();
        var supersededBy = (await db.UpdateSupersedence.AsNoTracking()
                .Where(s => releasedIds.Contains(s.SupersededUpdateId))
                .Select(s => new { s.SupersededUpdateId, s.UpdateId, s.Revision })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(s => s.SupersededUpdateId, s => (s.UpdateId, s.Revision));

        var spreads = await db.Rings.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.SpreadHours, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ids = clients.Select(c => c.Id).ToList();
        var available = await LoadAsync(db.ClientAvailableUpdates.Where(a => ids.Contains(a.ClientId)).Select(a => new ClientUpdateKey { ClientId = a.ClientId, UpdateId = a.UpdateId, Revision = a.Revision }), cancellationToken).ConfigureAwait(false);
        var installed = await LoadAsync(db.ClientUpdates.Where(a => ids.Contains(a.ClientId)).Select(a => new ClientUpdateKey { ClientId = a.ClientId, UpdateId = a.UpdateId, Revision = a.Revision }), cancellationToken).ConfigureAwait(false);
        var existing = (await db.Releases.Where(r => ids.Contains(r.ClientId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(r => r.ClientId);

        // Holds (Marc, 2026-10-27) withhold the releases of their ring, group or server.
        var holds = await UpdateHolds.ActiveAsync(db, now, cancellationToken).ConfigureAwait(false);
        var memberships = holds.Any(h => h.GroupId is not null)
            ? (await db.ClientGroups.AsNoTracking().Where(m => ids.Contains(m.ClientId)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(m => m.ClientId, m => m.GroupId)
            : Enumerable.Empty<ClientGroupEntity>().ToLookup(m => m.ClientId, m => m.GroupId);

        foreach (var client in clients)
        {
            var current = existing[client.Id].ToList();
            var desired = new Dictionary<(Guid, int), DateTime>();
            if (client.State == RegistrationState.Approved && client.RingId is { } ring)
            {
                var candidates = available[client.Id]
                    .Concat(current.Select(r => (r.UpdateId, r.Revision)).Where(u => !installed[client.Id].Contains(u)));
                foreach (var (updateId, revision) in candidates)
                {
                    if (released.TryGetValue((updateId, revision, ring), out var releasedAt))
                    {
                        desired[(updateId, revision)] = releasedAt + SpreadOffset(client.Id, updateId, spreads.GetValueOrDefault(ring));
                    }
                }

                var covered = desired.Keys.Concat(installed[client.Id]).ToHashSet();
                foreach (var key in desired.Keys.Where(k => supersededBy[k.Item1].Any(covered.Contains)).ToList())
                {
                    desired.Remove(key);
                }

                var groups = memberships[client.Id].ToHashSet();
                foreach (var held in holds.Where(h => h.RingId == ring || h.ClientId == client.Id || (h.GroupId is { } group && groups.Contains(group))))
                {
                    desired.Remove((held.UpdateId, held.Revision));
                }
            }

            var removed = current.Where(r => !desired.ContainsKey((r.UpdateId, r.Revision))).ToList();
            var added = desired.Where(d => !current.Any(r => (r.UpdateId, r.Revision) == d.Key)).ToList();
            if (removed.Count == 0 && added.Count == 0)
            {
                continue;
            }

            db.Releases.RemoveRange(removed);
            db.Releases.AddRange(added.Select(d => new ReleaseEntity
            {
                ClientId = client.Id,
                UpdateId = d.Key.Item1,
                Revision = d.Key.Item2,
                EarliestInstallAtUtc = d.Value,
                CreatedAtUtc = now,
            }));
            client.ReleasesVersion++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fixed offset within the ring spread, derived from server and update: the same server keeps its place for an update,
    /// and different updates spread differently, so no server is always last.
    /// </summary>
    internal static TimeSpan SpreadOffset(Guid clientId, Guid updateId, int spreadHours)
    {
        if (spreadHours <= 0)
        {
            return TimeSpan.Zero;
        }

        Span<byte> input = stackalloc byte[32];
        clientId.TryWriteBytes(input[..16]);
        updateId.TryWriteBytes(input[16..]);
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        var minutes = BitConverter.ToUInt32(hash, 0) % (uint)(spreadHours * 60);
        return TimeSpan.FromMinutes(minutes);
    }

    private static async Task<ILookup<Guid, (Guid UpdateId, int Revision)>> LoadAsync(IQueryable<ClientUpdateKey> query, CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToLookup(r => r.ClientId, r => (r.UpdateId, r.Revision));
    }

    private sealed class ClientUpdateKey
    {
        public Guid ClientId { get; init; }

        public Guid UpdateId { get; init; }

        public int Revision { get; init; }
    }
}
