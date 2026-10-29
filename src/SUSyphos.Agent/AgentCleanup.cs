using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Updates;

namespace SUSyphos.Agent;

/// <summary>
/// "SUSyphos.Agent.exe --cleanup", run by the installer when the agent is uninstalled (Marc, 2026-10-28): takes back the
/// Windows settings the agent changed, the same way as when the central server switches them off. Each manager removes
/// only its own change, recognised by its marker; group policy values are never touched. Never fails the uninstallation.
/// </summary>
internal static partial class AgentCleanup
{
    public const string Argument = "--cleanup";

    public static int Run(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AgentCleanup).FullName!);
        Try(logger, "Windows Automatic Updates", () => services.GetRequiredService<AutomaticUpdatesManager>().Apply(false));
        Try(logger, "Microsoft Update", () => services.GetRequiredService<MicrosoftUpdateManager>().Apply(false));
        return 0;
    }

    private static void Try(ILogger logger, string setting, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, setting, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Uninstall cleanup: {Setting} could not be restored.")]
    private static partial void LogFailed(ILogger logger, string setting, Exception exception);
}
