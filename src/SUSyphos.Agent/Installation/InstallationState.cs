using SUSyphos.Common;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Installation;

/// <summary>
/// Install reports not yet delivered and the last attempt per update. Persisted so that no result is lost
/// when the central server is unreachable, no update is retried in a loop after a service restart and a restart is not
/// requested again right after the server came back.
/// </summary>
internal sealed record InstallationState(
    List<InstallReport> Outbox,
    List<InstallAttempt> Attempts,
    DateTimeOffset? LastRebootRequestAt = null,
    LeaseHold? Lease = null,
    DateTimeOffset? RestartRequestedAt = null,
    InstallNowRequest? InstallNow = null,
    List<UninstallRequest>? Uninstalls = null,
    List<ActionOutcome>? ActionOutbox = null)
{
    public static InstallationState Empty => new([], []);
}

/// <summary>
/// An uninstallation an administrator asked for (remote action), held until it may run: in a coordinated group only with
/// the lease, and for <see cref="AgentActionKind.UninstallInWindow"/> only inside the maintenance window.
/// </summary>
internal sealed record UninstallRequest(Guid ActionId, AgentActionKind Kind, Guid UpdateId, string? KbArticle, DateTimeOffset RequestedAt);

/// <summary>The result of a held remote action, kept until the central server has it.</summary>
internal sealed record ActionOutcome(Guid ActionId, AgentActionResult Result);

/// <summary>An administrator asked to install the released updates now (remote action), with the restart right after or in the next window.</summary>
internal sealed record InstallNowRequest(DateTimeOffset RequestedAt, bool RestartNow);

internal sealed record InstallAttempt(Guid UpdateId, int Revision, DateTimeOffset AttemptedAt);

internal interface IInstallationStateStore
{
    InstallationState Load();

    void Save(InstallationState state);
}

internal sealed class ProtectedInstallationStateStore(string filePath, ILogger<ProtectedInstallationStateStore> logger) : IInstallationStateStore
{
    /// <summary>Default location: %ProgramData%\SUSyphos\Agent\installations.dat.</summary>
    public static string DefaultPath { get; } = Path.Combine(DataPaths.Agent, "installations.dat");

    private readonly ProtectedJsonFile<InstallationState> _file = new(
        new ProtectedFile(filePath, "SUSyphos.Agent.Installations.v1", logger),
        logger);

    public InstallationState Load()
    {
        var state = _file.Load();
        return state is { Outbox: not null, Attempts: not null } ? state : InstallationState.Empty;
    }

    public void Save(InstallationState state) => _file.Save(state);
}

/// <summary>A lease of a coordinated group the agent holds until installation and reboot are done.</summary>
/// <param name="AcquiredAt">When the lease was granted; a boot after it completes a pending reboot.</param>
/// <param name="InstallationSucceeded">Outcome so far, reported when the lease is released.</param>
/// <param name="AwaitingReboot">The lease is kept until the server has restarted.</param>
internal sealed record LeaseHold(DateTimeOffset AcquiredAt, bool InstallationSucceeded, bool AwaitingReboot);
