using System.Diagnostics;

namespace SUSyphos.Agent.Installation;

/// <summary>Restarts the server. Separate so tests never restart the machine.</summary>
internal interface ISystemRestart
{
    /// <summary>Schedules a restart after <paramref name="warning"/>; returns false if Windows refused it.</summary>
    bool Schedule(TimeSpan warning);
}

/// <summary>
/// Restarts through the Windows shutdown tool: it handles the shutdown privilege and the warning to signed-in users,
/// and records the restart as planned (reason p:2:17, operating system hotfix) in the system event log.
/// The arguments are constants and a number, never input from outside.
/// </summary>
internal sealed partial class ShutdownToolRestart(ILogger<ShutdownToolRestart> logger) : ISystemRestart
{
    private const string Message = "SUSyphos restarts this server to complete Windows updates inside the maintenance window.";

    public bool Schedule(TimeSpan warning)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/r", "/t", ((int)warning.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "/d", "p:2:17", "/c", Message })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                LogFailed(-1);
                return false;
            }

            if (process.ExitCode != 0)
            {
                // E.g. 1190 (ERROR_SHUTDOWN_IS_SCHEDULED): a restart is already pending.
                LogFailed(process.ExitCode);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogStartFailed(ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows did not accept the restart request (exit code {ExitCode}).")]
    private partial void LogFailed(int exitCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "The restart could not be requested.")]
    private partial void LogStartFailed(Exception exception);
}
