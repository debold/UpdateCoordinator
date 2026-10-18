using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Logging;

namespace UpdateCoordinator.Common.Logging;

/// <summary>
/// Rolling log files for troubleshooting. Bound from the "Logging:File" configuration section.
/// </summary>
public sealed class FileLoggingOptions
{
    public const string SectionName = "Logging:File";

    /// <summary>Folder of the log files; defaults to %ProgramData%\UpdateCoordinator\&lt;component&gt;\Logs.</summary>
    public string? Directory { get; set; }

    /// <summary>Lowest level written to the file.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Number of log files kept; older files are deleted. One file per day, more if the size limit is reached.</summary>
    [Range(1, 1_000)]
    public int RetainedFileCountLimit { get; set; } = 31;

    /// <summary>Maximum size of one file in bytes before a new file is started.</summary>
    [Range(1_048_576, 1_073_741_824)]
    public long FileSizeLimitBytes { get; set; } = 10 * 1_048_576;
}
