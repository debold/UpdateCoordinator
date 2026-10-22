using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>The newest known update that replaces another, with a short name that tells updates of the same KB apart.</summary>
internal sealed record NewerUpdate(Guid UpdateId, int Revision, string Name);

/// <summary>Which known updates replace others (Windows Update supersedence), for the dashboard.</summary>
internal static partial class Supersedence
{
    /// <summary>For each of <paramref name="updateIds"/> that a catalog update supersedes: the newest such update.</summary>
    public static async Task<IReadOnlyDictionary<Guid, NewerUpdate>> NewerAsync(ServerDbContext db, IReadOnlyCollection<Guid> updateIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(updateIds);
        if (updateIds.Count == 0)
        {
            return new Dictionary<Guid, NewerUpdate>();
        }

        var rows = await db.UpdateSupersedence.AsNoTracking()
            .Where(s => updateIds.Contains(s.SupersededUpdateId))
            .Join(db.Updates, s => new { s.UpdateId, s.Revision }, u => new { u.UpdateId, u.Revision }, (s, u) => new { s.SupersededUpdateId, u.UpdateId, u.Revision, u.KbArticle, u.Title, u.FirstSeenAtUtc })
            .ToListAsync()
            .ConfigureAwait(false);
        return rows
            .Where(r => r.UpdateId != r.SupersededUpdateId)
            .GroupBy(r => r.SupersededUpdateId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.FirstSeenAtUtc).Select(r => new NewerUpdate(r.UpdateId, r.Revision, ShortName(r.KbArticle, r.Title))).First());
    }

    /// <summary>"KB2267602 (Version 1.461.144.0)": the KB with the version from the title, since e.g. all definition updates share one KB.</summary>
    public static string ShortName(string? kbArticle, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var version = VersionRegex().Match(title);
        var name = kbArticle is null ? title : $"KB{kbArticle}";
        return version.Success && kbArticle is not null ? $"{name} (Version {version.Groups["version"].Value})" : name;
    }

    [GeneratedRegex(@"\(Version (?<version>[0-9][0-9.]{2,30})\)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VersionRegex();
}
