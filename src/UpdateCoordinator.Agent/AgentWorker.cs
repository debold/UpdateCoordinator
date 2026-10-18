namespace UpdateCoordinator.Agent;

/// <summary>
/// Main loop of the agent. Placeholder until register and heartbeat are implemented.
/// </summary>
internal sealed partial class AgentWorker(ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular service shutdown.
        }

        LogStopped();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent started.")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent stopped.")]
    private partial void LogStopped();
}
