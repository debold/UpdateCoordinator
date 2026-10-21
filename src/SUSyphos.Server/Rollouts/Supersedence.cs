using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>Which known updates replace others (Windows Update supersedence), for the dashboard.</summary>
internal static class Supersedence
{
    /// <summary>For each of <paramref name="updateIds"/> that a catalog update supersedes: the name of the newest such update ("KB5040001 ...").</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> NewerAsync(ServerDbContext db, IReadOnlyCollection<Guid> updateIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(updateIds);
        if (updateIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await db.UpdateSupersedence.AsNoTracking()
            .Where(s => updateIds.Contains(s.SupersededUpdateId))
            .Join(db.Updates, s => new { s.UpdateId, s.Revision }, u => new { u.UpdateId, u.Revision }, (s, u) => new { s.SupersededUpdateId, u.UpdateId, u.KbArticle, u.Title, u.FirstSeenAtUtc })
            .ToListAsync()
            .ConfigureAwait(false);
        return rows
            .Where(r => r.UpdateId != r.SupersededUpdateId)
            .GroupBy(r => r.SupersededUpdateId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.FirstSeenAtUtc).Select(r => r.KbArticle is null ? r.Title : $"KB{r.KbArticle}").First());
    }
}