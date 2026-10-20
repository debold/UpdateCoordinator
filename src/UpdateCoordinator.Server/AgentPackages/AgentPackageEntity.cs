namespace UpdateCoordinator.Server.AgentPackages;

internal enum AgentPackageSource
{
    /// <summary>Shipped with the central server installer.</summary>
    Bundled = 0,

    /// <summary>Downloaded from the GitHub releases of the project.</summary>
    GitHub = 1,
}

/// <summary>
/// An agent installer (MSI) the central server holds, and how far it is released for automatic agent updates
/// (see docs/04-central-server.md, agent updates).
/// </summary>
internal sealed class AgentPackageEntity
{
    /// <summary>Normalized semantic version, e.g. "0.4.0" (see <see cref="AgentVersion"/>).</summary>
    public required string Version { get; set; }

    public AgentPackageSource Source { get; set; }

    /// <summary>File name inside the package folder.</summary>
    public required string FileName { get; set; }

    /// <summary>SHA-256 of the file as lowercase hex; agents verify the download against it.</summary>
    public required string Sha256 { get; set; }

    public long Size { get; set; }

    public DateTime AddedAtUtc { get; set; }

    /// <summary>
    /// Agents in rings up to this order update to the package; null while not released. Releasing for all rings stores
    /// <see cref="AllRings"/>, so rings added later are included.
    /// </summary>
    public int? ReleasedThroughRingOrder { get; set; }

    public DateTime? ReleasedAtUtc { get; set; }

    public string? ReleasedBy { get; set; }

    public const int AllRings = int.MaxValue;
}
