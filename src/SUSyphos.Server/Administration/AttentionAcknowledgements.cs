using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Administration;

/// <summary>
/// Acknowledged topics of "Needs attention" on the overview (Marc, 2026-11-20): an operator hides a topic he knows about
/// (for example servers deliberately running past their end of support). It comes back on its own when more items are
/// affected than when it was acknowledged, and an acknowledgement ends when the topic is resolved, so the next time it
/// comes up it shows again. Acknowledged topics can be shown again from a list; every change is in the audit log.
/// </summary>
internal sealed partial class AttentionAcknowledgements(ServerDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    /// <summary>The acknowledgements by topic.</summary>
    public async Task<IReadOnlyDictionary<string, AttentionAcknowledgementEntity>> ListAsync(CancellationToken cancellationToken) =>
        await db.AttentionAcknowledgements.AsNoTracking().ToDictionaryAsync(a => a.Topic, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

    /// <summary>True when the topic is acknowledged and concerns no more items than then.</summary>
    public static bool Hides(IReadOnlyDictionary<string, AttentionAcknowledgementEntity> acknowledged, string topic, int count) =>
        acknowledged.TryGetValue(topic, out var entry) && count <= entry.AffectedCount;

    /// <summary>Acknowledges a topic with the number of items it concerns now; a second acknowledgement updates it.</summary>
    public async Task<Outcome> AcknowledgeAsync(string? topic, int count, string title, string actor, CancellationToken cancellationToken)
    {
        if (!IsTopic(topic) || count < 0)
        {
            return new Outcome("The entry is unknown.");
        }

        var entry = await db.AttentionAcknowledgements.SingleOrDefaultAsync(a => a.Topic == topic, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            entry = new AttentionAcknowledgementEntity { Topic = topic!, AcknowledgedBy = actor };
            db.AttentionAcknowledgements.Add(entry);
        }

        entry.AffectedCount = count;
        entry.AcknowledgedBy = actor;
        entry.AcknowledgedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Attention.Acknowledged", topic, string.Create(CultureInfo.InvariantCulture, $"{Shorten(title)} ({count})"), cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Shows an acknowledged topic again.</summary>
    public async Task<Outcome> RestoreAsync(string? topic, string actor, CancellationToken cancellationToken)
    {
        if (!IsTopic(topic) || await db.AttentionAcknowledgements.Where(a => a.Topic == topic).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            return new Outcome("The entry is not acknowledged.");
        }

        await audit.WriteAsync(actor, "Attention.Restored", topic, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Ends the acknowledgements of topics that are resolved (not among <paramref name="current"/>); returns how many.</summary>
    public async Task<int> PruneAsync(IReadOnlyCollection<string> current, CancellationToken cancellationToken)
    {
        var resolved = (await db.AttentionAcknowledgements.Select(a => a.Topic).ToListAsync(cancellationToken).ConfigureAwait(false)).Where(t => !current.Contains(t)).ToList();
        return resolved.Count == 0 ? 0 : await db.AttentionAcknowledgements.Where(a => resolved.Contains(a.Topic)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A topic key as the overview builds them: lower-case letters, digits and hyphens.</summary>
    public static bool IsTopic(string? topic) => topic is { Length: > 0 and <= AttentionAcknowledgementEntity.MaxTopicLength } && TopicPattern().IsMatch(topic);

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TopicPattern();
}
