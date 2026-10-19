using System.IO.Compression;

using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Actions;

/// <summary>Packs the agent's own log files for an upload requested by an administrator.</summary>
internal interface ILogCollector
{
    /// <summary>A ZIP archive with the newest log files of the last days within the size limit, and the number of files in it.</summary>
    (byte[] Archive, int Files) Collect();
}

/// <summary>
/// Collects only files matching the agent log pattern from the agent log folder, newest first. Log files that are still
/// being written are read with shared access. Older files are left out until the archive fits the upload limit.
/// </summary>
internal sealed class LogCollector(string directory, TimeProvider timeProvider) : ILogCollector
{
    public const string FilePattern = "agent-*.log";

    public (byte[] Archive, int Files) Collect()
    {
        if (!Directory.Exists(directory))
        {
            return (Pack([]), 0);
        }

        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-AgentActionList.LogDays);
        var files = new DirectoryInfo(directory)
            .EnumerateFiles(FilePattern, SearchOption.TopDirectoryOnly)
            .Where(f => f.LastWriteTimeUtc >= since)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        for (var count = files.Count; count > 0; count--)
        {
            var archive = Pack(files.Take(count).ToList());
            if (archive.Length <= AgentActionList.MaxLogBytes)
            {
                return (archive, count);
            }
        }

        return (Pack([]), 0);
    }

    private static byte[] Pack(IReadOnlyList<FileInfo> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                using var target = entry.Open();
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                source.CopyTo(target);
            }
        }

        return buffer.ToArray();
    }
}
