using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>
/// Antivirus definition updates (Defender and others) are expected to arrive several times a day from whoever starts
/// them first: SUSyphos, Defender itself or the Windows Update orchestrator, which Windows does not report reliably
/// (Marc, 2026-10-25). They are recognised by their classification only, never by who installed them, and are not
/// treated as installations outside SUSyphos, failures worth a notification or rollout news.
/// </summary>
internal static class DefinitionUpdates
{
    /// <summary>Classification Windows Update reports for antivirus definitions.</summary>
    public const string Classification = "Definition Updates";

    /// <summary>How long a server with Defender may go without a new signature before the critical notification.</summary>
    public static readonly TimeSpan MaxSignatureAge = TimeSpan.FromHours(48);

    public static bool IsClassification(string? classification) =>
        string.Equals(classification, Classification, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback for history entries whose update the catalog does not know: the titles Microsoft uses for signature
    /// updates ("Security Intelligence Update for Microsoft Defender Antivirus …", older "Definition Update for …").
    /// </summary>
    public static bool IsTitle(string? title) =>
        title is not null && TitlePatterns.Any(p => title.Contains(p.Trim('%'), StringComparison.OrdinalIgnoreCase));

    /// <summary>The title fragments as SQL LIKE patterns, for queries over the history.</summary>
    public static readonly string[] TitlePatterns = ["%Security Intelligence Update%", "%Definition Update%"];

    /// <summary>The update IDs among <paramref name="updateIds"/> the catalog classifies as definition updates.</summary>
    public static async Task<HashSet<Guid>> CatalogIdsAsync(ServerDbContext db, IEnumerable<Guid> updateIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ids = updateIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : (await db.Updates.AsNoTracking()
                .Where(u => ids.Contains(u.UpdateId) && u.Classification == Classification)
                .Select(u => u.UpdateId)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
    }

    /// <summary>True for a history entry or update that is a definition update by catalog or, unknown there, by title.</summary>
    public static bool Is(Guid updateId, string? title, IReadOnlySet<Guid> catalogIds) =>
        catalogIds.Contains(updateId) || IsTitle(title);
}
