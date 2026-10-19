using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Server.Data;
using UpdateCoordinator.Server.Trust;

namespace UpdateCoordinator.Server.Administration;

/// <summary>Contents description of a backup archive.</summary>
internal sealed record BackupManifest(int Format, DateTime CreatedAtUtc, string ServerVersion, string AnchorPublicKey);

/// <summary>
/// Backup and restore of the central server (docs/04-central-server.md, backup): one ZIP archive with a consistent copy of
/// the database (SQLite online backup) and the trust anchor encrypted with a backup password (PKCS#8, AES-256, PBKDF2 with
/// SHA-256). Without the password the anchor cannot be used; without the anchor every agent would have to be reset, so
/// the archive restores a central server on new hardware with all agents still trusting it.
/// </summary>
internal sealed class BackupService(TrustAnchor anchor, IOptions<DatabaseOptions> database, AuditLog audit, TimeProvider timeProvider)
{
    public const int FormatVersion = 1;
    public const int MinimumPasswordLength = 16;
    public const string ManifestEntry = "manifest.json";
    public const string DatabaseEntry = "server.db";
    public const string AnchorEntry = "trust-anchor.p8";

    /// <summary>Upper bound of an archive accepted for restore.</summary>
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;

    public static string? CheckPassword(string? password) =>
        password is null || password.Length < MinimumPasswordLength
            ? $"The backup password needs at least {MinimumPasswordLength} characters."
            : null;

    /// <summary>Writes the backup archive to <paramref name="target"/>.</summary>
    public async Task<Outcome> CreateAsync(Stream target, string? password, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (CheckPassword(password) is { } error)
        {
            return new Outcome(error);
        }

        var copy = Path.Combine(Path.GetTempPath(), $"UpdateCoordinator-backup-{Guid.NewGuid():N}.db");
        try
        {
            // Online backup: a consistent copy while the service keeps writing.
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Value.Path, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            var manifest = new BackupManifest(FormatVersion, timeProvider.GetUtcNow().UtcDateTime, ServerVersion, Convert.ToBase64String(anchor.PublicKey));
            using (var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
            {
                await using (var entry = await zip.CreateEntry(ManifestEntry).OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(entry, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                await using (var entry = await zip.CreateEntry(AnchorEntry, CompressionLevel.NoCompression).OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    await entry.WriteAsync(anchor.ExportEncrypted(password!), cancellationToken).ConfigureAwait(false);
                }

                await zip.CreateEntryFromFileAsync(copy, DatabaseEntry, CompressionLevel.Optimal, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(copy);
        }

        await audit.WriteAsync(actor, "Backup.Created", "central server", null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>
    /// Restores an archive into the given database and anchor paths. Runs while the central server service is stopped
    /// (command line: <c>UpdateCoordinator.Server.exe restore</c>). Nothing is written unless the archive is complete and
    /// the password decrypts the anchor.
    /// </summary>
    public static async Task<Outcome> RestoreAsync(string archivePath, string? password, string databasePath, string anchorPath, CancellationToken cancellationToken)
    {
        if (CheckPassword(password) is { } error)
        {
            return new Outcome(error);
        }

        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length > MaxArchiveBytes)
        {
            return new Outcome("The backup archive does not exist or is too large.");
        }

        try
        {
            using var zip = await ZipFile.OpenReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
            var manifestEntry = zip.GetEntry(ManifestEntry);
            var anchorEntry = zip.GetEntry(AnchorEntry);
            var databaseEntry = zip.GetEntry(DatabaseEntry);
            if (manifestEntry is null || anchorEntry is null || databaseEntry is null)
            {
                return new Outcome("The archive is not a backup of the central server.");
            }

            BackupManifest? manifest;
            await using (var stream = await manifestEntry.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (manifest is null || manifest.Format != FormatVersion)
            {
                return new Outcome("The backup format is not supported by this version.");
            }

            byte[] encrypted;
            await using (var stream = await anchorEntry.OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                encrypted = buffer.ToArray();
            }

            // The database is extracted next to its target first, so a failure leaves the current one untouched.
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
            Directory.CreateDirectory(directory);
            var staged = Path.Combine(directory, $"restore-{Guid.NewGuid():N}.db");
            try
            {
                await databaseEntry.ExtractToFileAsync(staged, overwrite: false, cancellationToken).ConfigureAwait(false);

                byte[] pkcs8;
                byte[] publicKey;
                try
                {
                    (pkcs8, publicKey) = TrustAnchor.DecryptBackup(encrypted, password!);
                }
                catch (CryptographicException)
                {
                    return new Outcome("The password is wrong or the trust anchor in the archive is damaged.");
                }

                if (Convert.ToBase64String(publicKey) != manifest.AnchorPublicKey)
                {
                    return new Outcome("The trust anchor does not match the manifest of the archive.");
                }

                // Everything checked: only now the current anchor and database are replaced.
                TrustAnchor.StoreRestored(anchorPath, pkcs8);
                File.Move(staged, databasePath, overwrite: true);
            }
            finally
            {
                File.Delete(staged);
            }

            return Outcome.Ok;
        }
        catch (InvalidDataException)
        {
            return new Outcome("The archive is damaged.");
        }
        catch (JsonException)
        {
            return new Outcome("The archive's manifest is damaged.");
        }
    }

    private static string ServerVersion =>
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
