using System.IO.Compression;
using System.Security.Cryptography;

namespace SUSyphos.Contracts.Scripts;

/// <summary>One file in the ZIP of a script revision, as the approval page lists it.</summary>
/// <param name="Path">Relative path inside the ZIP, with "/" as separator.</param>
/// <param name="Size">Unpacked size in bytes.</param>
/// <param name="Sha256">SHA-256 of the unpacked file, upper-case hex.</param>
public sealed record ScriptFileEntry(string Path, long Size, string Sha256);

/// <summary>
/// The ZIP of files a script revision brings along (Marc, 2026-11-22, for example the configuration of BGInfo), checked the
/// same way by the central server on upload and by the agent before it unpacks: at most <see cref="ScriptLimits.MaxFiles"/>
/// entries and <see cref="ScriptLimits.MaxFilesUnpackedBytes"/> unpacked, only relative paths without "..", drive letters,
/// alternate data streams or links. The agent unpacks only a ZIP whose SHA-256 the signed revision names.
/// </summary>
public static class ScriptFilesFormat
{
    /// <summary>The environment variable that names the folder with the unpacked files.</summary>
    public const string EnvironmentVariable = "SUSYPHOS_FILES";

    /// <summary>The folder of the run the files go to.</summary>
    public const string FolderName = "files";

    /// <summary>Reads every entry and checks the rules; returns the files or the reason the ZIP is refused.</summary>
    public static (IReadOnlyList<ScriptFileEntry> Files, string? Error) Inspect(Stream zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var files = new List<ScriptFileEntry>();
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > ScriptLimits.MaxFiles)
            {
                return ([], $"The ZIP has {archive.Entries.Count} entries; at most {ScriptLimits.MaxFiles} are allowed.");
            }

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (CheckName(entry) is { } refused)
                {
                    return ([], refused);
                }

                if (IsFolder(entry))
                {
                    continue;
                }

                using var stream = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long size = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    size += read;
                    total += read;
                    if (total > ScriptLimits.MaxFilesUnpackedBytes)
                    {
                        return ([], $"The files take more than {ScriptLimits.MaxFilesUnpackedBytes / (1024 * 1024)} MB when unpacked.");
                    }

                    hash.AppendData(buffer, 0, read);
                }

                files.Add(new ScriptFileEntry(Normalize(entry.FullName), size, Convert.ToHexString(hash.GetHashAndReset())));
            }

            return files.Count == 0 ? ([], "The ZIP contains no file.") : (files, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
        {
            return ([], $"The file is no valid ZIP: {exception.Message}");
        }
    }

    /// <summary>
    /// Unpacks the ZIP into <paramref name="target"/> (created if missing) with the same checks, counting the real bytes
    /// written, never following a path outside the folder. Returns null on success, otherwise the reason; then the folder
    /// may hold part of the files and the caller deletes it.
    /// </summary>
    public static string? Extract(Stream zip, string target)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentException.ThrowIfNullOrEmpty(target);
        var root = Path.GetFullPath(target);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > ScriptLimits.MaxFiles)
            {
                return $"The ZIP has more than {ScriptLimits.MaxFiles} entries.";
            }

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (CheckName(entry) is { } refused)
                {
                    return refused;
                }

                var path = Path.GetFullPath(Path.Combine(root, Normalize(entry.FullName).Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return $"The entry {entry.FullName} lies outside the folder.";
                }

                if (IsFolder(entry))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = entry.Open();
                using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > ScriptLimits.MaxFilesUnpackedBytes)
                    {
                        return $"The files take more than {ScriptLimits.MaxFilesUnpackedBytes / (1024 * 1024)} MB when unpacked.";
                    }

                    destination.Write(buffer, 0, read);
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return $"The files could not be unpacked: {exception.Message}";
        }
    }

    private static bool IsFolder(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static string Normalize(string name) => name.Replace('\\', '/').TrimEnd('/');

    /// <summary>Why an entry name is refused; null when it is a plain relative path.</summary>
    internal static string? CheckName(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        if (name.Length == 0 || name.Length > 260)
        {
            return "The ZIP contains an entry without a name or with a name longer than 260 characters.";
        }

        // Unix symbolic links carry the file type in the upper half of the external attributes.
        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
        {
            return $"The entry {name} is a link; links are not allowed.";
        }

        if (name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal) || name.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return $"The entry {name} has an absolute path or characters Windows does not allow.";
        }

        var parts = name.TrimEnd('/').Split('/');
        return parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' '))
            ? $"The entry {name} has a path with \".\", \"..\" or an empty part."
            : null;
    }
}
