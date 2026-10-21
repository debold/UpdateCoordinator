using Microsoft.Extensions.Options;

using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Installation;
using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Updates;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Actions;

/// <summary>Remote action step of the main loop.</summary>
internal interface IActionRunner
{
    /// <summary>
    /// Fetches and runs the actions the heartbeat announced and reports each result.
    /// Returns false if the central server rejected the credentials.
    /// </summary>
    Task<bool> RunAnnouncedAsync(AgentIdentity identity, HeartbeatResponse heartbeat, CancellationToken cancellationToken);
}

/// <summary>Ends the agent process so the service control manager starts it again.</summary>
internal interface IServiceRestarter
{
    void RequestRestart();
}

/// <summary>
/// Stops the host with a failure exit code: the service recovery settings of the agent service restart it
/// (restart on failure, including non-crash failures).
/// </summary>
internal sealed class HostServiceRestarter(IHostApplicationLifetime lifetime) : IServiceRestarter
{
    public void RequestRestart()
    {
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }
}

/// <summary>
/// Runs remote actions (docs/05-agent-design.md, remote actions). Most actions only trigger steps the agent performs anyway;
/// a server restart is an explicit administrator request that still respects the local veto.
/// </summary>
internal sealed partial class ActionRunner(
    ICentralServerClient client,
    IUpdateReporter updates,
    IInventoryReporter inventory,
    IServiceRestarter restarter,
    ISystemRestart systemRestart,
    IInstallationRunner installationRunner,
    ILogCollector logs,
    IOptions<InstallationOptions> installation,
    ILogger<ActionRunner> logger) : IActionRunner
{
    public async Task<bool> RunAnnouncedAsync(AgentIdentity identity, HeartbeatResponse heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        if (heartbeat.State != RegistrationState.Approved || heartbeat.PendingActions <= 0)
        {
            return true;
        }

        var list = await client.GetActionsAsync(identity, cancellationToken).ConfigureAwait(false);
        if (list is null)
        {
            return false;
        }

        var restart = false;
        foreach (var action in list.Actions)
        {
            var (result, restartAfter, accepted) = await RunAsync(identity, action, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                return false;
            }

            LogAction(action.Id, action.Kind, result.Status);
            if (!await client.SendActionResultAsync(identity, action.Id, result, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            restart |= restartAfter;
        }

        // The restart comes last, after every result was delivered.
        if (restart)
        {
            restarter.RequestRestart();
        }

        return true;
    }

    /// <summary>Runs one action; Accepted is false if the central server rejected the credentials during it.</summary>
    private async Task<(AgentActionResult Result, bool Restart, bool Accepted)> RunAsync(AgentIdentity identity, AgentAction action, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case AgentActionKind.RestartServer:
                // An explicit administrator request bypasses policy and window, never the local veto.
                if (!installation.Value.AllowReboot)
                {
                    return (new AgentActionResult { Status = AgentActionStatus.Rejected, Detail = "Reboots are forbidden on this server (AllowReboot)." }, false, true);
                }

                return systemRestart.Schedule(Installation.RebootPlanner.WarningPeriod)
                    ? (Succeeded("The server restarts in 5 minutes."), false, true)
                    : (new AgentActionResult { Status = AgentActionStatus.Failed, Detail = "Windows did not accept the restart request." }, false, true);
            case AgentActionKind.RestartServerInWindow:
                if (!installation.Value.AllowReboot)
                {
                    return (new AgentActionResult { Status = AgentActionStatus.Rejected, Detail = "Reboots are forbidden on this server (AllowReboot)." }, false, true);
                }

                return (Succeeded(installationRunner.RequestRestart() is { } planned
                    ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Restart planned for the next maintenance window, {planned.UtcDateTime:yyyy-MM-dd HH:mm} UTC (if the window still applies then).")
                    : "Restart requested; no maintenance window is configured, so it waits until one applies."), false, true);
            case AgentActionKind.InstallNow or AgentActionKind.InstallNowRestartInWindow:
                var restartNow = action.Kind == AgentActionKind.InstallNow;
                installationRunner.RequestInstallNow(restartNow);
                var restartText = !installation.Value.AllowReboot
                    ? "Reboots are forbidden on this server (AllowReboot), so a needed restart is left to an administrator."
                    : restartNow ? "A needed restart follows right after, with a five minute warning." : "A needed restart waits for the next maintenance window.";
                return (Succeeded($"The released updates install with this cycle, also outside the maintenance window. {restartText}"), false, true);
            case AgentActionKind.CancelRestart:
                return (Succeeded(installationRunner.CancelRestart()), false, true);
            case AgentActionKind.UploadLogs:
                byte[] archive;
                int files;
                try
                {
                    (archive, files) = logs.Collect();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (new AgentActionResult { Status = AgentActionStatus.Failed, Detail = "The log files could not be read: " + ex.GetType().Name }, false, true);
                }

                if (!await client.UploadLogsAsync(identity, action.Id, archive, cancellationToken).ConfigureAwait(false))
                {
                    return (Succeeded(string.Empty), false, false);
                }

                return (Succeeded(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{files} log files uploaded ({archive.Length / 1024} KB).")), false, true);
            case AgentActionKind.SearchNow:
                updates.RequestRun();
                return (Succeeded("The update search runs with this cycle."), false, true);
            case AgentActionKind.FullResync:
                inventory.Reset();
                updates.RequestRun();
                return (Succeeded("Inventory and update search run with this cycle."), false, true);
            case AgentActionKind.RestartService:
                return (Succeeded("The agent service restarts."), true, true);
            default:
                // A newer central server may know actions this agent does not.
                return (new AgentActionResult { Status = AgentActionStatus.Rejected, Detail = "This agent version does not know the action." }, false, true);
        }
    }

    private static AgentActionResult Succeeded(string detail) => new() { Status = AgentActionStatus.Succeeded, Detail = detail };

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote action {ActionId} ({Kind}): {Status}.")]
    private partial void LogAction(Guid actionId, AgentActionKind kind, AgentActionStatus status);
}
