namespace SUSyphos.Common;

/// <summary>
/// The product was called UpdateCoordinator before it was renamed to SUSyphos (2026-10-20). Installations of those versions
/// keep their data under %ProgramData%\UpdateCoordinator; the services take it over once at start.
/// </summary>
public static class LegacyData
{
    /// <summary>Name used before the rename, in data folders, registry keys and policy keys.</summary>
    public const string ProductName = "UpdateCoordinator";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ProductName);

    /// <summary>
    /// Moves <paramref name="legacyDirectory"/> to <paramref name="directory"/> if only the legacy one exists, keeping its
    /// access control. Returns true if it moved something. Never overwrites existing data.
    /// </summary>
    public static bool MoveDataFolder(string legacyDirectory, string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(legacyDirectory);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (Directory.Exists(directory) || !Directory.Exists(legacyDirectory))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Directory.Move(legacyDirectory, directory);
        return true;
    }
}

/// <summary>Logs once at start that the data of a version from before the rename was taken over.</summary>
public sealed partial class LegacyDataNotice(Microsoft.Extensions.Logging.ILogger<LegacyDataNotice> logger) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogMoved(LegacyData.Root);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Took over the data of the previous version from {LegacyRoot} (the product was renamed to SUSyphos).")]
    private partial void LogMoved(string legacyRoot);
}
