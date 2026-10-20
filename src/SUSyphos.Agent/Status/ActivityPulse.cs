using Microsoft.Extensions.Options;

using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Identity;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Status;

/// <summary>
/// Sends the live status while a long step runs (docs/05-agent-design.md, section 5). The main loop waits for a download
/// or installation, so without this the central server would see neither progress nor a sign of life. Sends only while
/// an activity is running and once more when it ended; idle agents cause no extra traffic. Errors are never fatal.
/// </summary>
internal sealed partial class ActivityPulse(
    ActivityTracker tracker,
    ICentralServerClient client,
    IAgentIdentityStore identityStore,
    IOptions<CentralServerOptions> options,
    TimeProvider timeProvider,
    ILogger<ActivityPulse> logger) : BackgroundService
{
    /// <summary>A running activity is repeated at least this often, so the central server knows it is still current.</summary>
    internal static readonly TimeSpan RepeatAfter = TimeSpan.FromSeconds(30);

    private long _sentVersion;
    private bool _sentActive;
    private DateTimeOffset _sentAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.Value.HeartbeatInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                await SendIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or System.Security.Authentication.AuthenticationException)
            {
                // The next heartbeat or pulse tries again; the live status is informative only.
                LogPulseFailed(ex.Message);
            }
        }
    }

    /// <summary>Sends the activity if it changed, if it ended or if a running one was not repeated for a while.</summary>
    internal async Task<bool> SendIfDueAsync(CancellationToken cancellationToken)
    {
        var version = tracker.Version;
        var current = tracker.Current;
        var now = timeProvider.GetUtcNow();
        var due = current is not null
            ? version != _sentVersion || !_sentActive || now - _sentAt >= RepeatAfter
            : _sentActive;
        if (!due || identityStore.Load() is not { } identity)
        {
            return false;
        }

        await client.SendActivityAsync(identity, new AgentActivityReport { Activity = current }, cancellationToken).ConfigureAwait(false);
        _sentVersion = version;
        _sentActive = current is not null;
        _sentAt = now;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Live status not delivered: {Reason}")]
    private partial void LogPulseFailed(string reason);
}
