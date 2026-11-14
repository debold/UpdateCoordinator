using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SUSyphos.Common.Diagnostics;

/// <summary>A part of a diagnostic archive: files from a folder, or a text produced while packing.</summary>
public abstract record ArchivePart;

/// <summary>Files matching <paramref name="Pattern"/> directly in <paramref name="Directory"/> (no subfolders), stored below <paramref name="Prefix"/>.</summary>
public sealed record LogSource(string Directory, string Pattern, string Prefix) : ArchivePart;

/// <summary>A text produced while packing (an event log extract), stored as <paramref name="Name"/>; an exception is noted in collection.txt.</summary>
public sealed record ArchiveText(string Name, Func<string> Content) : ArchivePart;

/// <summary>The packed archive and how many files and texts it holds besides collection.txt.</summary>
public sealed record DiagnosticArchiveResult(byte[] Archive, int Files);

/// <summary>
/// Packs logs for troubleshooting (agent log upload and the central server's log download, Marc 2026-11-14): the parts in
/// the given order, each folder newest first and only files changed in the last <c>days</c>, until the archive reaches its
/// size limit; the rest is left out. Files still being written are read with shared access. Every archive starts with
/// <c>collection.txt</c>, which lists what is in it, which folders were missing and what was left out and why, so a
/// missing log is never a mystery. Measures the compressed size first and packs once, so memory stays near the limit.
/// </summary>
public sealed class DiagnosticArchive(TimeProvider timeProvider, int days, long maxBytes)
{
    public const string CollectionFile = "collection.txt";

    /// <summary>Space kept free for collection.txt; it is cut to fit when a collection lists very many files.</summary>
    private const int CollectionReserveBytes = 64 * 1024;
    private const long EmptyArchiveBytes = 64;
    private const long EntryOverheadBytes = 128;

    public DiagnosticArchiveResult Build(string title, IReadOnlyList<ArchivePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-days);
        var notes = new StringBuilder();
        var candidates = new List<Candidate>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case LogSource source when !Directory.Exists(source.Directory):
                    notes.AppendLine(CultureInfo.InvariantCulture, $"Missing folder: {source.Directory} ({source.Pattern})");
                    break;
                case LogSource source:
                    try
                    {
                        candidates.AddRange(new DirectoryInfo(source.Directory)
                            .EnumerateFiles(source.Pattern, SearchOption.TopDirectoryOnly)
                            .Where(f => f.LastWriteTimeUtc >= since)
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .Select(f => new Candidate(source.Prefix + f.Name, f, null)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        notes.AppendLine(CultureInfo.InvariantCulture, $"Folder not readable: {source.Directory} ({ex.Message})");
                    }

                    break;
                case ArchiveText text:
                    try
                    {
                        candidates.Add(new Candidate(text.Name, null, Encoding.UTF8.GetBytes(text.Content())));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        notes.AppendLine(CultureInfo.InvariantCulture, $"Not produced: {text.Name} ({ex.GetType().Name}: {ex.Message})");
                    }

                    break;
            }
        }

        candidates = [.. candidates.DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];

        var selected = new List<Candidate>();
        var leftOut = new List<Candidate>();
        var size = EmptyArchiveBytes + CollectionReserveBytes;
        foreach (var candidate in candidates)
        {
            long entry;
            try
            {
                entry = CompressedSize(candidate) + EntryOverheadBytes + (2L * candidate.Name.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.AppendLine(CultureInfo.InvariantCulture, $"Not readable: {candidate.File?.FullName} ({ex.Message})");
                continue;
            }

            if (leftOut.Count > 0 || size + entry > maxBytes)
            {
                // Keep the order: once one file does not fit, the later (older or less important) ones stay out too.
                leftOut.Add(candidate);
                continue;
            }

            selected.Add(candidate);
            size += entry;
        }

        // A file still being written may have grown since it was measured; then the last files are left out.
        for (var count = selected.Count; count >= 0; count--)
        {
            var collection = Collection(title, now, since, selected[..count], [.. selected[count..], .. leftOut], notes.ToString());
            var archive = Pack(collection, selected[..count]);
            if (archive.Length <= maxBytes || count == 0)
            {
                return new DiagnosticArchiveResult(archive, count);
            }
        }

        throw new InvalidOperationException("Unreachable.");
    }

    private sealed record Candidate(string Name, FileInfo? File, byte[]? Text);

    private string Collection(string title, DateTime now, DateTime since, IReadOnlyList<Candidate> included, IReadOnlyList<Candidate> leftOut, string notes)
    {
        var text = new StringBuilder()
            .AppendLine(title)
            .AppendLine(CultureInfo.InvariantCulture, $"Collected (UTC): {now:yyyy-MM-dd HH:mm:ss} on {Environment.MachineName}")
            .AppendLine(CultureInfo.InvariantCulture, $"Period: files changed since {since:yyyy-MM-dd HH:mm} UTC ({days} days); size limit {maxBytes / (1024 * 1024)} MB")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Included ({included.Count}):");
        foreach (var file in included)
        {
            text.AppendLine(Describe(file));
        }

        if (leftOut.Count > 0)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Left out to stay below the size limit ({leftOut.Count}):");
            foreach (var file in leftOut)
            {
                text.AppendLine(Describe(file));
            }
        }

        if (notes.Length > 0)
        {
            text.AppendLine().AppendLine("Notes:").Append(notes);
        }

        var result = text.ToString();
        return result.Length <= CollectionReserveBytes / 2 ? result : result[..(CollectionReserveBytes / 2)] + Environment.NewLine + "(cut)";
    }

    private static string Describe(Candidate candidate) => candidate.File is { } file
        ? string.Create(CultureInfo.InvariantCulture, $"  {candidate.Name}  {file.Length:N0} bytes  changed {file.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC  from {file.FullName}")
        : string.Create(CultureInfo.InvariantCulture, $"  {candidate.Name}  {candidate.Text!.Length:N0} bytes  produced while collecting");

    /// <summary>Size of the entry after compression, without keeping the compressed bytes.</summary>
    private static long CompressedSize(Candidate candidate)
    {
        using var counter = new CountingStream();
        using (var deflate = new DeflateStream(counter, CompressionLevel.Optimal, leaveOpen: true))
        using (var source = Open(candidate))
        {
            source.CopyTo(deflate);
        }

        return counter.Length;
    }

    private static Stream Open(Candidate candidate) => candidate.File is { } file
        ? new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
        : new MemoryStream(candidate.Text!, writable: false);

    private static byte[] Pack(string collection, IReadOnlyList<Candidate> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(CollectionFile, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
            {
                writer.Write(collection);
            }

            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                using var target = entry.Open();
                using var source = Open(file);
                source.CopyTo(target);
            }
        }

        return buffer.ToArray();
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
}
