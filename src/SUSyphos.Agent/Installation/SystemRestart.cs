using System.Diagnostics;

namespace SUSyphos.Agent.Installation;

/// <summary>Restarts the server. Separate so tests never restart the machine.</summary>
internal interface ISystemRestart
{
    /// <summary>Schedules a restart after <paramref name="warning"/>; returns false if Windows refused it.</summary>
    bool Schedule(TimeSpan warning);

    /// <summary>Cancels a restart announced with <see cref="Schedule"/>; returns false if none was pending or Windows refused.</summary>
    bool Abort();

    /// <summary>When the restart announced with <see cref="Schedule"/> happens; null when none is announced or it is overdue (cancelled elsewhere, e.g. shutdown /a).</summary>
    DateTimeOffset? AnnouncedFor { get; }
}

/// <summary>
/// The restart announced to Windows, for the heartbeat: when it happens. A restart this much past its time did not
/// happen (cancelled outside the agent, e.g. shutdown /a), so it is forgotten.
/// </summary>
internal sealed class RestartAnnouncement(TimeProvider timeProvider)
{
    internal static readonly TimeSpan Overdue = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _at;

    public DateTimeOffset? At => _at is { } at && timeProvider.GetUtcNow() - at < Overdue ? at : null;

    public void Announce(TimeSpan warning) => _at = timeProvider.GetUtcNow() + warning;

    public void Clear() => _at = null;
}

/// <summary>
/// Restarts through the Windows shutdown tool: it handles the shutdown privilege and the warning to signed-in users,
/// and records the restart as planned (reason p:2:17, operating system hotfix) in the system event log.
/// The arguments are constants, a number and the message from the central settings (checked: no control characters or quotes), passed as separate arguments.
/// </summary>
internal sealed partial class ShutdownToolRestart(Microsoft.Extensions.Options.IOptions<InstallationOptions> options, TimeProvider timeProvider, ILogger<ShutdownToolRestart> logger) : ISystemRestart
{
    private const string Message = "SUSyphos restarts this server to complete Windows updates inside the maintenance window.";

    private readonly RestartAnnouncement _announcement = new(timeProvider);

    public DateTimeOffset? AnnouncedFor => _announcement.At;

    public bool Schedule(TimeSpan warning)
    {
        var accepted = Run(["/r", "/t", ((int)warning.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "/d", "p:2:17", "/c", options.Value.RestartMessage is { Length: > 0 } custom ? custom : Message]);
        if (accepted)
        {
            _announcement.Announce(warning);
        }

        return accepted;
    }

    public bool Abort()
    {
        var aborted = Run(["/a"]);
        if (aborted)
        {
            _announcement.Clear();
        }

        return aborted;
    }

    private bool Run(string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
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
