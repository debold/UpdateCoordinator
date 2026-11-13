using SUSyphos.Common;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Installation;

/// <summary>
/// Install reports not yet delivered and the last attempt per update. Persisted so that no result is lost
/// when the central server is unreachable, no update is retried in a loop after a service restart and a restart is not
/// requested again right after the server came back. <c>RestartRefusedCode</c>: Windows refused the last restart request
/// (exit code of the shutdown tool), until the server has started again.
/// </summary>
internal sealed record InstallationState(
    List<InstallReport> Outbox,
    List<InstallAttempt> Attempts,
    DateTimeOffset? LastRebootRequestAt = null,
    LeaseHold? Lease = null,
    DateTimeOffset? RestartRequestedAt = null,
    InstallNowRequest? InstallNow = null,
    List<UninstallRequest>? Uninstalls = null,
    List<ActionOutcome>? ActionOutbox = null,
    int? RestartRefusedCode = null)
{
    public static InstallationState Empty => new([], []);
}

/// <summary>
/// An uninstallation an administrator asked for (remote action), held until it may run: in a coordinated group only with
/// the lease, and for <see cref="AgentActionKind.UninstallInWindow"/> only inside the maintenance window.
/// </summary>
internal sealed record UninstallRequest(Guid ActionId, AgentActionKind Kind, Guid UpdateId, string? KbArticle, DateTimeOffset RequestedAt, string? PackageId = null);

/// <summary>The result of a held remote action, kept until the central server has it.</summary>
internal sealed record ActionOutcome(Guid ActionId, AgentActionResult Result);

/// <summary>
/// An administrator asked to install the released updates now (remote action), with the restart right after or in the
/// next window; <paramref name="Scope"/> limits it to Windows updates or software (null in states from before 0.9: all).
/// </summary>
internal sealed record InstallNowRequest(DateTimeOffset RequestedAt, bool RestartNow, InstallScope? Scope = null);

/// <summary>The last attempt to install an update; <paramref name="Succeeded"/>: Windows reported it installed (null in states from before 0.9.0-rc.3).</summary>
internal sealed record InstallAttempt(Guid UpdateId, int Revision, DateTimeOffset AttemptedAt, bool? Succeeded = null);

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
