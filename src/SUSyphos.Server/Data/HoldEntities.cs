namespace SUSyphos.Server.Data;

/// <summary>
/// An update held back for one ring, group or server (Marc, 2026-10-27): releases not installed there yet are withheld,
/// and a held ring does not count as done for the next ring. Exactly one of <see cref="RingId"/>, <see cref="GroupId"/>
/// and <see cref="ClientId"/> is set; deleting the target deletes the hold.
/// </summary>
internal sealed class UpdateHoldEntity
{
    public const int MaxReasonLength = 200;

    public int Id { get; set; }

    public Guid UpdateId { get; set; }

    public int Revision { get; set; }

    public int? RingId { get; set; }

    public int? GroupId { get; set; }

    public Guid? ClientId { get; set; }

    public string? Reason { get; set; }

    /// <summary>The hold ends by itself after this time; null keeps it until someone resumes the update.</summary>
    public DateTime? UntilUtc { get; set; }

    public required string CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public bool IsActive(DateTime nowUtc) => UntilUtc is not { } until || until > nowUtc;
}

/// <summary>
/// A maintenance window for one date and time span (Marc, 2026-10-27), in addition to the repeating windows: global when
/// none of <see cref="RingId"/>, <see cref="GroupId"/> and <see cref="ClientId"/> is set, otherwise for exactly one of them.
/// Valid for every released update; blackouts still win.
/// </summary>
internal sealed class OneTimeWindowEntity
{
    public const int MaxReasonLength = 200;

    /// <summary>Longest one-time window.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(7);

    /// <summary>Ended windows are deleted after this time; the audit log keeps them.</summary>
    public static readonly TimeSpan KeepAfterEnd = TimeSpan.FromDays(30);

    public int Id { get; set; }

    public int? RingId { get; set; }

    public int? GroupId { get; set; }

    public Guid? ClientId { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public string? Reason { get; set; }

    public required string CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
