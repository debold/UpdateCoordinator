namespace SUSyphos.Server;

/// <summary>
/// Runs the central server and writes a startup failure to the log before the service ends, so an administrator sees
/// why the service stopped (missing certificate, unreadable trust anchor, invalid configuration) instead of an empty log.
/// </summary>
internal static partial class ServerStartup
{
    public static async Task RunAsync(WebApplication app, Func<WebApplication, Task> initialize)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(initialize);
        try
        {
            await initialize(app).ConfigureAwait(false);
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(app.Logger, ex);
            throw;
        }
        finally
        {
            // Flushes the file log.
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The central server could not start or stopped because of an error.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
