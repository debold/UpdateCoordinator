namespace UpdateCoordinator.Server.Data;

/// <summary>
/// Installation history per server and update (see docs/04-central-server.md, section 2).
/// </summary>
internal sealed class InstallationEntity
{
    public long Id { get; set; }

    public Guid ClientId { get; set; }

    /// <summary>Installation run reported by the agent; the same run is stored only once.</summary>
    public Guid RunId { get; set; }

    public Guid UpdateId { get; set; }

    public int Revision { get; set; }

    public int ResultCode { get; set; }

    public int HResult { get; set; }

    public bool RebootRequired { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime FinishedAtUtc { get; set; }

    /// <summary>"Coordinator" for installations of this solution; external installations follow with history sync.</summary>
    public required string Source { get; set; }
}
