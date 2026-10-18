using UpdateCoordinator.Common;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Installation;

/// <summary>
/// Install reports not yet delivered and the last attempt per update. Persisted so that no result is lost
/// when the central server is unreachable, no update is retried in a loop after a service restart and a restart is not
/// requested again right after the server came back.
/// </summary>
internal sealed record InstallationState(List<InstallReport> Outbox, List<InstallAttempt> Attempts, DateTimeOffset? LastRebootRequestAt = null)
{
    public static InstallationState Empty => new([], []);
}

internal sealed record InstallAttempt(Guid UpdateId, int Revision, DateTimeOffset AttemptedAt);

internal interface IInstallationStateStore
{
    InstallationState Load();

    void Save(InstallationState state);
}

internal sealed class ProtectedInstallationStateStore(string filePath, ILogger<ProtectedInstallationStateStore> logger) : IInstallationStateStore
{
    /// <summary>Default location: %ProgramData%\UpdateCoordinator\Agent\installations.dat.</summary>
    public static string DefaultPath { get; } = Path.Combine(DataPaths.Agent, "installations.dat");

    private readonly ProtectedJsonFile<InstallationState> _file = new(
        new ProtectedFile(filePath, "UpdateCoordinator.Agent.Installations.v1", logger),
        logger);

    public InstallationState Load()
    {
        var state = _file.Load();
        return state is { Outbox: not null, Attempts: not null } ? state : InstallationState.Empty;
    }

    public void Save(InstallationState state) => _file.Save(state);
}
