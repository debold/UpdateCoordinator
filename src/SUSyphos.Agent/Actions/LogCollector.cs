using System.IO.Compression;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Actions;

/// <summary>Packs the agent's own log files for an upload requested by an administrator.</summary>
internal interface ILogCollector
{
    /// <summary>A ZIP archive with the newest log files of the last days within the size limit, and the number of files in it.</summary>
    (byte[] Archive, int Files) Collect();
}

/// <summary>A further folder whose log files go into the archive, below <paramref name="Prefix"/>.</summary>
internal sealed record LogSource(string Directory, string Pattern, string Prefix);

/// <summary>
/// Collects only files matching the agent log pattern from the agent log folder, newest first, then the files of the
/// further sources (the WinGet helper and WinGet's own logs, lab 2026-11-10). Log files that are still being written are
/// read with shared access. Older files are left out until the archive fits the upload limit; the agent's own logs come first.
/// </summary>
internal sealed class LogCollector(string directory, TimeProvider timeProvider, IReadOnlyList<LogSource>? sources = null) : ILogCollector
{
    public const string FilePattern = "agent-*.log";

    /// <summary>The WinGet logs next to the agent log: the helper's steps, the installers' logs and WinGet's own logs of SYSTEM.</summary>
    public static IReadOnlyList<LogSource> WinGetSources(string agentLogDirectory) =>
    [
        new(Path.Combine(agentLogDirectory, "WinGet"), "*.log", "WinGet/"),
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemTemp", "WinGet", "defaultState"), "WinGetCOM-*.log", "WinGetCOM/"),
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "WinGet", "defaultState"), "WinGetCOM-*.log", "WinGetCOM/"),
    ];

    public (byte[] Archive, int Files) Collect()
    {
        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-AgentActionList.LogDays);
        var files = new[] { new LogSource(directory, FilePattern, string.Empty) }.Concat(sources ?? [])
            .Where(source => Directory.Exists(source.Directory))
            .SelectMany(source => new DirectoryInfo(source.Directory)
                .EnumerateFiles(source.Pattern, SearchOption.TopDirectoryOnly)
                .Where(f => f.LastWriteTimeUtc >= since)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => (File: f, Name: source.Prefix + f.Name)))
            .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Measure first, pack once: building candidate archives in memory would need several times the upload limit
        // (measured: about 370 MB for 80 MB of poorly compressible logs), far beyond the agent's memory limit.
        var selected = new List<(FileInfo File, string Name)>();
        long size = EmptyArchiveBytes;
        foreach (var file in files)
        {
            var entry = CompressedSize(file.File) + EntryOverheadBytes + (2L * file.Name.Length);
            if (size + entry > AgentActionList.MaxLogBytes)
            {
                break;
            }

            selected.Add(file);
            size += entry;
        }

        // A file still being written may have grown since it was measured; then the oldest files are left out.
        for (var count = selected.Count; count > 0; count--)
        {
            var archive = Pack(selected[..count]);
            if (archive.Length <= AgentActionList.MaxLogBytes)
            {
                return (archive, count);
            }
        }

        return (Pack([]), 0);
    }

    private const long EmptyArchiveBytes = 64;
    private const long EntryOverheadBytes = 128;

    /// <summary>Size of the file after compression, without keeping the compressed bytes.</summary>
    private static long CompressedSize(FileInfo file)
    {
        using var counter = new CountingStream();
        using (var deflate = new DeflateStream(counter, CompressionLevel.Optimal, leaveOpen: true))
        using (var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            source.CopyTo(deflate);
        }

        return counter.Length;
    }

    /// <summary>Write-only stream that only counts the bytes written to it.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => _length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte[] Pack(IReadOnlyList<(FileInfo File, string Name)> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                using var target = entry.Open();
                using var source = new FileStream(file.File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                source.CopyTo(target);
            }
        }

        return buffer.ToArray();
    }
}
