
using SUSyphos.Common.Logging;

namespace SUSyphos.Agent.Diagnostics;

/// <summary>
/// The debug mode an administrator switches on for this server (Marc, 2026-11-21): the central server sends its end with
/// every heartbeat. While it runs, the agent logs at Debug, WinGet writes its verbose log (the reasons why an installer
/// does not fit), and a log collection adds WindowsUpdate.log, CBS.log and the DISM log. It ends by itself at its end time,
/// also when the central server cannot be reached.
/// </summary>
internal sealed partial class AgentDebugMode(TimeProvider timeProvider, ILogger<AgentDebugMode> logger, FileLogLevel? fileLevel = null)
{
    private DateTimeOffset? _until;

    /// <summary>End of the debug mode; null when it is off.</summary>
    public DateTimeOffset? Until => IsActive ? _until : null;

    public bool IsActive => _until is { } until && until > timeProvider.GetUtcNow();

    /// <summary>Takes the end the central server sent (null: off) and sets the log level accordingly.</summary>
    public void Apply(DateTimeOffset? until)
    {
        _until = until;
        Refresh();
    }

    /// <summary>Ends an expired debug mode (called with every cycle, also without a heartbeat).</summary>
    public void Refresh()
    {
        var active = IsActive;
        if (fileLevel?.SetDebug(active) == true)
        {
            if (active)
            {
                LogOn(_until!.Value);
            }
            else
            {
                LogOff();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Debug mode on until {Until:u}: the agent logs at Debug, WinGet writes its verbose log, log collections add Windows Update, CBS and DISM logs.")]
    private partial void LogOn(DateTimeOffset until);

    [LoggerMessage(Level = LogLevel.Information, Message = "Debug mode off; the log level is back as configured.")]
    private partial void LogOff();
}
