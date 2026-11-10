using System.Globalization;

namespace SUSyphos.Agent.WinGet;

/// <summary>
/// Step lines of the helper with their time: on standard error, where the agent writes them into its log as they come,
/// and in a daily file in the WinGet log folder, which stays readable even when the agent stops a hanging helper.
/// </summary>
internal sealed class HelperTrace(TextWriter error, string? logFolder)
{
    /// <summary>Daily trace files older than this are deleted when the helper starts.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private readonly Lock _lock = new();

    public void Write(string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}");
        lock (_lock)
        {
            error.WriteLine(line);
            error.Flush();
            if (logFolder is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(logFolder);
                File.AppendAllText(Path.Combine(logFolder, string.Create(CultureInfo.InvariantCulture, $"helper-{DateTime.Now:yyyyMMdd}.log")), line + Environment.NewLine);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The trace on standard error is enough; a locked or full disk must not stop the installation.
            }
        }
    }

    /// <summary>Deletes trace files older than <see cref="Retention"/>; WinGet's own logs are left alone.</summary>
    public static void Clean(string logFolder, DateTime now)
    {
        try
        {
            foreach (var file in Directory.Exists(logFolder) ? Directory.EnumerateFiles(logFolder, "helper-*.log") : [])
            {
                if (now - File.GetLastWriteTime(file) > Retention)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleaning up is a courtesy.
        }
    }
}
