using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Identity;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Updates;

/// <summary>
/// Keeps the releases of the central server in sync. The heartbeat announces the current version;
/// the list is only fetched when it changed. Used by the installation step.
/// </summary>
internal interface IReleaseSync
{
    ReleaseList Current { get; }

    /// <summary>Global pause announced by the central server with the last heartbeat.</summary>
    bool InstallationsPaused { get; }

    /// <summary>Applies the control information of a heartbeat and fetches the release list if the version changed. Returns false if the credentials were rejected.</summary>
    Task<bool> SyncAsync(AgentIdentity identity, HeartbeatResponse heartbeat, CancellationToken cancellationToken);
}

internal sealed partial class ReleaseSync(ICentralServerClient client, ILogger<ReleaseSync> logger, Configuration.CentralSettings? settings = null) : IReleaseSync
{
    private static readonly ReleaseList Empty = new(0, []);

    public ReleaseList Current { get; private set; } = Empty;

    public bool InstallationsPaused { get; private set; }

    public async Task<bool> SyncAsync(AgentIdentity identity, HeartbeatResponse heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        var announcedVersion = heartbeat.ReleasesVersion;
        InstallationsPaused = heartbeat.InstallationsPaused;
        if (announcedVersion is null)
        {
            // Not approved (any more): nothing may be installed.
            Current = Empty;
            return true;
        }

        if (announcedVersion == Current.Version && Current != Empty)
        {
            return true;
        }

        var releases = await client.GetReleasesAsync(identity, cancellationToken).ConfigureAwait(false);
        if (releases is null)
        {
            return false;
        }

        Current = releases;
        settings?.Apply(releases.AgentSettings);
        LogSynced(releases.Version, releases.Releases.Count);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Release list version {Version} received ({Count} released updates).")]
    private partial void LogSynced(long version, int count);
}
