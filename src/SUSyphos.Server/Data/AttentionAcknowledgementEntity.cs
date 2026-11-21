namespace SUSyphos.Server.Data;

/// <summary>
/// A topic of "Needs attention" on the overview that a user acknowledged (Marc, 2026-11-20): it stays hidden while the
/// number of affected items does not rise, and the row is deleted as soon as the topic is resolved, so it shows again the
/// next time it comes up.
/// </summary>
internal sealed class AttentionAcknowledgementEntity
{
    public const int MaxTopicLength = 64;

    /// <summary>Key of the topic, e.g. "support-ended" (one kind of entry, not one server).</summary>
    public required string Topic { get; set; }

    /// <summary>Number of affected items when it was acknowledged; more brings the topic back.</summary>
    public int AffectedCount { get; set; }

    public required string AcknowledgedBy { get; set; }

    public DateTime AcknowledgedAtUtc { get; set; }
}
