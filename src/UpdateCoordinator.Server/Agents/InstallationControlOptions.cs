namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Central installation control. Bound from the "Installation" configuration section of the central server
/// until the dashboard offers it.
/// </summary>
internal sealed class InstallationControlOptions
{
    public const string SectionName = "Installation";

    /// <summary>Global pause: agents start no new installations; check, search and reporting continue.</summary>
    public bool GlobalPause { get; set; }
}
