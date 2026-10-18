using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Updates;

/// <summary>
/// Keeps the releases of the central server in sync. The heartbeat announces the current version;
/// the list is only fetched when it changed. Used by the installation step.
/// </summary>
internal interface IReleaseSync
{
    ReleaseList Current { get; }

    /// <summary>Fetches the release list if <paramref name="announcedVersion"/> differs. Returns false if the credentials were rejected.</summary>
    Task<bool> SyncAsync(AgentIdentity identity, long? announcedVersion, CancellationToken cancellationToken);
}

internal sealed partial class ReleaseSync(ICentralServerClient client, ILogger<ReleaseSync> logger) : IReleaseSync
{
    private static readonly ReleaseList Empty = new(0, []);

    public ReleaseList Current { get; private set; } = Empty;

    public async Task<bool> SyncAsync(AgentIdentity identity, long? announcedVersion, CancellationToken cancellationToken)
    {
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
        LogSynced(releases.Version, releases.Releases.Count);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Release list version {Version} received ({Count} released updates).")]
    private partial void LogSynced(long version, int count);
}
