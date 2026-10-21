using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>
/// The update catalog is built from agent reports (see docs/01-architecture.md, section 5).
/// Shared by inventory (installed updates) and update report (available updates).
/// </summary>
internal sealed class UpdateCatalog(ServerDbContext db)
{
    /// <summary>
    /// Adds catalog entries that are not known yet and fills details (size, reboot behavior, severity)
    /// that were unknown so far. Changes are saved by the caller.
    /// </summary>
    public async Task EnsureAsync(IReadOnlyCollection<UpdateInfo> updates, DateTime now, CancellationToken cancellationToken)
    {
        var ids = updates.Select(u => u.UpdateId).Distinct().ToList();
        var known = await db.Updates
            .Where(u => ids.Contains(u.UpdateId))
            .ToDictionaryAsync(u => (u.UpdateId, u.Revision), cancellationToken)
            .ConfigureAwait(false);
        var reported = updates.OfType<AvailableUpdate>().Where(u => u.SupersededUpdateIds is not null).Select(u => u.UpdateId).Distinct().ToList();
        var superseded = reported.Count == 0
            ? []
            : (await db.UpdateSupersedence.AsNoTracking().Where(s => reported.Contains(s.UpdateId)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(s => (s.UpdateId, s.Revision, s.SupersededUpdateId)).ToHashSet();

        foreach (var update in updates.DistinctBy(u => (u.UpdateId, u.Revision)))
        {
            if (!known.TryGetValue((update.UpdateId, update.Revision), out var entry))
            {
                entry = new UpdateEntity
                {
                    UpdateId = update.UpdateId,
                    Revision = update.Revision,
                    KbArticle = update.KbArticle,
                    Title = update.Title,
                    Classification = update.Classification,
                    FirstSeenAtUtc = now,
                };
                db.Updates.Add(entry);
                known[(update.UpdateId, update.Revision)] = entry;
            }

            if (update is AvailableUpdate available)
            {
                // Needed again: back in the lists.
                entry.LastNeededAtUtc = now;
                entry.ArchivedAtUtc = null;
                entry.MaxDownloadSizeBytes ??= available.MaxDownloadSizeBytes;
                entry.RebootBehavior ??= (int?)available.RebootBehavior;
                entry.MsrcSeverity ??= available.MsrcSeverity;

                // Supersedence of a revision does not change; new pairs are added, none removed.
                foreach (var replaced in available.SupersededUpdateIds ?? [])
                {
                    if (superseded.Add((update.UpdateId, update.Revision, replaced)))
                    {
                        db.UpdateSupersedence.Add(new UpdateSupersedenceEntity { UpdateId = update.UpdateId, Revision = update.Revision, SupersededUpdateId = replaced });
                    }
                }
            }
        }
    }
}
