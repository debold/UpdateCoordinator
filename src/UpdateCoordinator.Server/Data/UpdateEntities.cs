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

    public DateTime FirstSeenAtUtc { get; set; }
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
