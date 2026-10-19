using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.Inventory;
using UpdateCoordinator.Agent.Updates;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Actions;

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
/// Runs remote actions (docs/05-agent-design.md, remote actions). Actions only trigger steps the agent performs anyway,
/// so an action can never do more than the agent's own schedule allows.
/// </summary>
internal sealed partial class ActionRunner(
    ICentralServerClient client,
    IUpdateReporter updates,
    IInventoryReporter inventory,
    IServiceRestarter restarter,
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
            var (result, restartAfter) = Run(action);
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

    private (AgentActionResult Result, bool Restart) Run(AgentAction action)
    {
        switch (action.Kind)
        {
            case AgentActionKind.SearchNow:
                updates.RequestRun();
                return (Succeeded("The update search runs with this cycle."), false);
            case AgentActionKind.FullResync:
                inventory.Reset();
                updates.RequestRun();
                return (Succeeded("Inventory and update search run with this cycle."), false);
            case AgentActionKind.RestartService:
                return (Succeeded("The agent service restarts."), true);
            default:
                // A newer central server may know actions this agent does not.
                return (new AgentActionResult { Status = AgentActionStatus.Rejected, Detail = "This agent version does not know the action." }, false);
        }
    }

    private static AgentActionResult Succeeded(string detail) => new() { Status = AgentActionStatus.Succeeded, Detail = detail };

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote action {ActionId} ({Kind}): {Status}.")]
    private partial void LogAction(Guid actionId, AgentActionKind kind, AgentActionStatus status);
}
