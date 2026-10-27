using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>An installed update the uninstall dialog offers: when it was installed and whether Windows Update can remove it.</summary>
internal sealed record UninstallCandidate(Guid UpdateId, string? Kb, string Title, DateTime? InstalledAtUtc, bool? Uninstallable);

/// <summary>
/// The installed updates of a server that can be uninstalled, newest installation first. Defender signatures and its
/// platform, and tools such as the Malicious Software Removal Tool are left out: they cannot be removed this way and
/// the next update brings them back anyway.
/// </summary>
internal static class UninstallCandidates
{
    /// <summary>
    /// Title parts of updates that cannot be removed this way. Cumulative updates for .NET Framework bundle one package per
    /// framework version under other KB numbers: Windows Update refuses to uninstall them and DISM finds no package of the KB
    /// the dialog names (seen in the lab, Marc 2026-10-27), so they are not offered.
    /// </summary>
    private static readonly string[] NotRemovable = ["Malicious Software Removal Tool", "antimalware platform", "Cumulative Update for .NET Framework"];

    public static bool IsRemovable(string? classification, string title) =>
        !DefinitionUpdates.IsClassification(classification)
        && !DefinitionUpdates.IsTitle(title)
        && !NotRemovable.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));

    public static async Task<List<UninstallCandidate>> LoadAsync(ServerDbContext db, Guid clientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var installed = await db.ClientUpdates.AsNoTracking()
            .Where(c => c.ClientId == clientId)
            .Join(db.Updates, c => new { c.UpdateId, c.Revision }, u => new { u.UpdateId, u.Revision }, (c, u) => new { u.UpdateId, u.KbArticle, u.Title, u.Classification, c.Uninstallable })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var installedAt = await db.ClientUpdateHistory.AsNoTracking()
            .Where(h => h.ClientId == clientId && h.Operation == UpdateOperation.Installation)
            .GroupBy(h => h.UpdateId)
            .Select(g => new { UpdateId = g.Key, At = g.Max(h => h.DateUtc) })
            .ToDictionaryAsync(h => h.UpdateId, h => h.At, cancellationToken).ConfigureAwait(false);

        return [.. installed
            .Where(u => IsRemovable(u.Classification, u.Title))
            .DistinctBy(u => u.UpdateId)
            .Select(u => new UninstallCandidate(u.UpdateId, u.KbArticle, u.Title, installedAt.TryGetValue(u.UpdateId, out var at) ? at : null, u.Uninstallable))
            .OrderBy(u => u.InstalledAtUtc is null)
            .ThenByDescending(u => u.InstalledAtUtc)
            .ThenBy(u => u.Title, StringComparer.CurrentCultureIgnoreCase)];
    }
}
