using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

namespace UpdateCoordinator.Server.Rollouts;

/// <summary>Settings of the ring logic, bound from the "Rollout" configuration section.</summary>
internal sealed class RolloutOptions
{
    public const string SectionName = "Rollout";

    /// <summary>How often rollouts are evaluated and releases synchronized.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Evaluates the ring transitions of all active rollouts periodically. A failed run is logged and retried with the
/// next interval; it never stops the service.
/// </summary>
internal sealed partial class RolloutWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<RolloutOptions> options,
    TimeProvider timeProvider,
    ILogger<RolloutWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogRunFailed(ex);
            }

            try
            {
                await Task.Delay(options.CurrentValue.EvaluationInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RolloutService>().AdvanceAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Rollout evaluation failed; retrying with the next interval.")]
    private partial void LogRunFailed(Exception exception);
}
