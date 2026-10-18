namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Central installation control, bound from the "Installation" configuration section of the central server.
/// The global pause is normally switched in the dashboard; this setting forces it on.
/// </summary>
internal sealed class InstallationControlOptions
{
    public const string SectionName = "Installation";

    /// <summary>Forces the global pause on, independent of the dashboard switch (see <see cref="GlobalPause"/>).</summary>
    public bool GlobalPause { get; set; }
}
