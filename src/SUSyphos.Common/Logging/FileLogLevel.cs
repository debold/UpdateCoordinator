using Microsoft.Extensions.Logging;

using Serilog.Core;
using Serilog.Events;

namespace SUSyphos.Common.Logging;

/// <summary>
/// The level of the file log, changeable while the service runs: the debug mode of agent and central server (Marc,
/// 2026-11-21) raises it to Debug for a while and sets it back to the configured level afterwards.
/// </summary>
public sealed class FileLogLevel(LogLevel configured)
{
    internal LoggingLevelSwitch Switch { get; } = new(FileLoggingExtensions.ToSerilog(configured));

    /// <summary>The level from the configuration ("Logging:File:MinimumLevel").</summary>
    public LogLevel Configured { get; } = configured;

    /// <summary>True while the file log writes debug messages because of the debug mode.</summary>
    public bool DebugActive { get; private set; }

    /// <summary>Switches the debug level on or off; off returns to the configured level. True when it changed.</summary>
    public bool SetDebug(bool on)
    {
        if (on == DebugActive)
        {
            return false;
        }

        DebugActive = on;
        Switch.MinimumLevel = on && Configured > LogLevel.Debug ? LogEventLevel.Debug : FileLoggingExtensions.ToSerilog(Configured);
        return true;
    }
}
