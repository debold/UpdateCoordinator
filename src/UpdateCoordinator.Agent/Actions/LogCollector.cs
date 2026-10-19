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

        // Measure first, pack once: building candidate archives in memory would need several times the upload limit
        // (measured: about 370 MB for 80 MB of poorly compressible logs), far beyond the agent's memory limit.
        var selected = new List<FileInfo>();
        long size = EmptyArchiveBytes;
        foreach (var file in files)
        {
            var entry = CompressedSize(file) + EntryOverheadBytes + (2L * file.Name.Length);
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
