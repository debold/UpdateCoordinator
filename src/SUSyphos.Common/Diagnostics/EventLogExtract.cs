using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security;
using System.Text;

namespace SUSyphos.Common.Diagnostics;

/// <summary>
/// The entries of the Application event log that explain failed installations and crashes (Marc, 2026-11-14), as text
/// for the diagnostic archives: Windows Installer (for example 1033 and 11708 with the product and its error), crashes
/// and hangs of programs, .NET runtime errors, Windows Error Reporting and the SUSyphos services. Newest first, at most
/// <see cref="MaxEvents"/> entries.
/// </summary>
public static class EventLogExtract
{
    public const int MaxEvents = 2000;

    /// <summary>Event sources of the extract: installers, crashes, .NET, error reporting and SUSyphos.</summary>
    public static readonly IReadOnlyList<string> Providers =
    [
        "MsiInstaller",
        "Application Error",
        "Application Hang",
        ".NET Runtime",
        "Windows Error Reporting",
        "SUSyphos.Agent",
        "SUSyphos.Server",
    ];

    /// <summary>The XPath query of the extract; built only from the fixed provider names and a UTC time.</summary>
    public static string Query(DateTime sinceUtc)
    {
        var providers = string.Join(" or ", Providers.Select(p => $"Provider[@Name='{p}']"));
        return string.Create(CultureInfo.InvariantCulture, $"*[System[({providers}) and TimeCreated[@SystemTime>='{sinceUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
    }

    /// <summary>Reads the extract; a log that cannot be read gives a text saying why instead of an exception.</summary>
    public static string Read(DateTime sinceUtc, string logName = "Application")
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Event log {logName} since {sinceUtc.ToUniversalTime():yyyy-MM-dd HH:mm} UTC, newest first; sources: {string.Join(", ", Providers)}")
            .AppendLine();
        var count = 0;
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, Query(sinceUtc)) { ReverseDirection = true });
            for (var record = reader.ReadEvent(); record is not null && count < MaxEvents; record = reader.ReadEvent())
            {
                using (record)
                {
                    text.AppendLine(Format(record));
                    count++;
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or SecurityException)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"The event log could not be read: {ex.Message}");
        }

        text.AppendLine().AppendLine(count == MaxEvents ? $"Stopped after {MaxEvents} entries." : $"{count} entries.");
        return text.ToString();
    }

    private static string Format(EventRecord record)
    {
        string? message;
        try
        {
            message = record.FormatDescription();
        }
        catch (EventLogException)
        {
            message = null;
        }

        // Without the message file of its source an entry still shows its inserted values.
        message ??= string.Join(" | ", record.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture)));
        var level = record.Level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            _ => "Info",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{record.TimeCreated?.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC  {level,-8} {record.ProviderName} {record.Id}{Environment.NewLine}    {message.ReplaceLineEndings(Environment.NewLine + "    ").TrimEnd()}");
    }
}
