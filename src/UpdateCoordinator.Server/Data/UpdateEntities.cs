namespace UpdateCoordinator.Server.Data;

/// <summary>
/// Update catalog entry, aggregated from agent reports (see docs/01-architecture.md, section 5).
/// Identified by update ID and revision.
/// </summary>
internal sealed class UpdateEntity
{
    public Guid UpdateId { get; set; }

    public int Revision { get; set; }

    public string? KbArticle { get; set; }

    public required string Title { get; set; }

    public string? Classification { get; set; }

    /// <summary>Set from update reports (search results); unknown for updates only seen as installed.</summary>
    public long? MaxDownloadSizeBytes { get; set; }

    public int? RebootBehavior { get; set; }

    public string? MsrcSeverity { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }

    /// <summary>Last time an approved server reported the update as missing; null if none ever did.</summary>
    public DateTime? LastNeededAtUtc { get; set; }

    /// <summary>Hidden from the update lists because no server has needed it for a while; the history stays.</summary>
    public DateTime? ArchivedAtUtc { get; set; }
}

/// <summary>
/// An update installed on a client, as last reported by its inventory.
/// </summary>
internal sealed class ClientUpdateEntity
{
    public Guid ClientId { get; set; }

    public Guid UpdateId { get; set; }

    public int Revision { get; set; }

    public DateTime ReportedAtUtc { get; set; }
}

/// <summary>
/// An update available (not installed) on a client, as last reported by its update search.
/// </summary>
internal sealed class ClientAvailableUpdateEntity
{
    public Guid ClientId { get; set; }

    public Guid UpdateId { get; set; }

    public int Revision { get; set; }

    public DateTime ReportedAtUtc { get; set; }
}

/// <summary>
/// An update released for a client, with the earliest installation time. Created by the ring logic
/// (<see cref="Rollouts.ReleasePlanner"/>); the client fetches its releases when <see cref="ClientEntity.ReleasesVersion"/> changes.
/// </summary>
internal sealed class ReleaseEntity
{
    public Guid ClientId { get; set; }

    public Guid UpdateId { get; set; }

    public int Revision { get; set; }

    public DateTime EarliestInstallAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Result of one pre-flight check, stored as JSON with the client.
/// </summary>
internal sealed class HealthCheckEntry
{
    public required string Name { get; set; }

    public int Status { get; set; }

    public string? Detail { get; set; }
}
