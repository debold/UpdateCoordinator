using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent;

/// <summary>
/// Main loop of the agent: registers once, then sends heartbeats. Backs off exponentially
/// while the central server is unreachable and never lets an error stop the service.
/// </summary>
internal sealed partial class AgentWorker(
    ICentralServerClient client,
    IAgentIdentityStore identityStore,
    IAgentEnvironment environment,
    IOptions<CentralServerOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private RegistrationState? _lastState;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(environment.AgentVersion);
        var settings = options.Value;
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                consecutiveFailures = 0;
                delay = settings.HeartbeatInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                consecutiveFailures++;
                delay = Backoff.Compute(consecutiveFailures, settings.HeartbeatInterval, settings.MaxBackoff);
                LogCycleFailed(ex, consecutiveFailures, delay);
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogStopped();
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var identity = identityStore.Load() ?? await RegisterAsync(cancellationToken).ConfigureAwait(false);

        var response = await client
            .HeartbeatAsync(identity, new HeartbeatRequest { AgentVersion = environment.AgentVersion }, cancellationToken)
            .ConfigureAwait(false);

        if (response is null)
        {
            // The central server no longer knows these credentials (e.g. deleted or database restored).
            // Registering again creates a new entry that needs approval, so no trust is gained.
            LogCredentialsRejected(identity.ClientId);
            identityStore.Delete();
            _lastState = null;
            return;
        }

        if (response.State != _lastState)
        {
            LogStateChanged(identity.ClientId, response.State);
            _lastState = response.State;
        }
    }

    private async Task<AgentIdentity> RegisterAsync(CancellationToken cancellationToken)
    {
        var request = new RegisterRequest
        {
            HostName = environment.HostName,
            DomainName = environment.DomainName,
            OsVersion = environment.OsVersion,
            AgentVersion = environment.AgentVersion,
        };

        var identity = await client.RegisterAsync(request, cancellationToken).ConfigureAwait(false);
        identityStore.Save(identity);
        LogRegistered(identity.ClientId);
        return identity;
    }

    /// <summary>Every error except fatal runtime conditions is logged and retried; the service must keep running.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Guideline: a single error must never stop the service.")]
    private static bool IsRecoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {Version} started.")]
    private partial void LogStarted(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent stopped.")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered at the central server as client {ClientId}.")]
    private partial void LogRegistered(Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registration state of client {ClientId} is {State}.")]
    private partial void LogStateChanged(Guid clientId, RegistrationState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The central server rejected the credentials of client {ClientId}; the agent will register again.")]
    private partial void LogCredentialsRejected(Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Communication with the central server failed ({Failures} in a row); next attempt in {Delay}.")]
    private partial void LogCycleFailed(Exception exception, int failures, TimeSpan delay);
}
