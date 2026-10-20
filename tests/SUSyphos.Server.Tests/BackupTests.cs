using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Trust;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

public sealed class BackupTests : IDisposable
{
    private const string BackupPassword = "a long backup password!";
    private const string Password = "correct horse battery";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Backup_ContainsManifestAnchorAndDatabase_AndIsAudited()
    {
        await using var factory = new ServerFactory();
        var archive = await CreateBackupAsync(factory);

        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.Equal(
                [BackupService.ManifestEntry, BackupService.DatabaseEntry, BackupService.AnchorEntry],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            using var manifest = JsonDocument.Parse(zip.GetEntry(BackupService.ManifestEntry)!.Open());
            Assert.Equal(BackupService.FormatVersion, manifest.RootElement.GetProperty("Format").GetInt32());
            Assert.Equal(Convert.ToBase64String(factory.Services.GetRequiredService<TrustAnchor>().PublicKey), manifest.RootElement.GetProperty("AnchorPublicKey").GetString());
        }

        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "Backup.Created" && e.Actor == "admin", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("fifteen chars!!")]
    public async Task Backup_WithShortPassword_IsRefused(string? password)
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        await using var scope = factory.Services.CreateAsyncScope();
        using var target = new MemoryStream();

        var outcome = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync(target, password, "admin", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public async Task Restore_RecreatesDatabaseAndTrustAnchor()
    {
        await using var factory = new ServerFactory();
        var archive = await CreateBackupAsync(factory);
        var publicKey = factory.Services.GetRequiredService<TrustAnchor>().PublicKey;
        var (databasePath, anchorPath) = Targets();

        var outcome = await RestoreAsync(archive, BackupPassword, databasePath, anchorPath);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.True(File.Exists(anchorPath + TrustAnchor.RestoreSuffix));

        // The service takes the staged key over on its next start and protects it for its own account.
        var restored = new TrustAnchor(Options.Create(new TrustOptions { AnchorPath = anchorPath }), NullLogger<TrustAnchor>.Instance);
        Assert.Equal(publicKey, restored.PublicKey);
        Assert.True(File.Exists(anchorPath));
        Assert.False(File.Exists(anchorPath + TrustAnchor.RestoreSuffix));
        Assert.Equal(publicKey, new TrustAnchor(Options.Create(new TrustOptions { AnchorPath = anchorPath }), NullLogger<TrustAnchor>.Instance).PublicKey);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AuditEntries WHERE Action = 'Backup.Created'";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task Restore_WithWrongPassword_ChangesNothing()
    {
        await using var factory = new ServerFactory();
        var archive = await CreateBackupAsync(factory);
        var (databasePath, anchorPath) = Targets();
        await File.WriteAllTextAsync(databasePath, "current database", TestContext.Current.CancellationToken);

        var outcome = await RestoreAsync(archive, "another long password", databasePath, anchorPath);

        Assert.False(outcome.Succeeded);
        Assert.Equal("current database", await File.ReadAllTextAsync(databasePath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(anchorPath + TrustAnchor.RestoreSuffix));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(databasePath)!));
    }

    public static TheoryData<string> DamagedArchives => ["not a zip", "missing anchor", "missing database", "wrong format", "foreign anchor", "broken manifest"];

    [Theory]
    [MemberData(nameof(DamagedArchives))]
    public async Task Restore_RefusesDamagedOrForeignArchives(string damage)
    {
        await using var factory = new ServerFactory();
        var archive = await CreateBackupAsync(factory);
        await DamageAsync(archive, damage);
        var (databasePath, anchorPath) = Targets();

        var outcome = await RestoreAsync(archive, BackupPassword, databasePath, anchorPath);

        Assert.False(outcome.Succeeded);
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(anchorPath + TrustAnchor.RestoreSuffix));
    }

    [Fact]
    public async Task Restore_RefusesMissingFileAndShortPassword()
    {
        var (databasePath, anchorPath) = Targets();

        Assert.False((await RestoreAsync(Path.Combine(_directory, "missing.zip"), BackupPassword, databasePath, anchorPath)).Succeeded);
        Assert.False((await RestoreAsync(Path.Combine(_directory, "missing.zip"), "short", databasePath, anchorPath)).Succeeded);
    }

    [Fact]
    public async Task Download_NeedsAdministrator_AndMatchingPasswords()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("operator", Password, AdminRoles.Operator);
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);

        using (var operatorBrowser = new DashboardClient(factory))
        {
            using (await operatorBrowser.LoginAsync("operator", Password))
            {
            }

            using var refused = await operatorBrowser.GetAsync("/administration/backup");
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        }

        using var admin = new DashboardClient(factory);
        using (await admin.LoginAsync("admin", Password))
        {
        }

        using (var differing = await Download(admin, BackupPassword, BackupPassword + "x"))
        {
            Assert.Equal(HttpStatusCode.Redirect, differing.StatusCode);
            Assert.Contains("error=", differing.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        using (var tooShort = await Download(admin, "short", "short"))
        {
            Assert.Equal(HttpStatusCode.Redirect, tooShort.StatusCode);
        }

        using var file = await Download(admin, BackupPassword, BackupPassword);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/zip", file.Content.Headers.ContentType?.MediaType);
        var bytes = await file.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal("PK"u8.ToArray(), bytes[..2]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp folder never fails a test.
        }
    }

    private static Task<HttpResponseMessage> Download(DashboardClient client, string password, string repeat) =>
        client.PostFormAsync(BackupEndpoints.Route, new Dictionary<string, string> { ["password"] = password, ["passwordRepeat"] = repeat }, pageToRead: "/administration/backup");

    private async Task<string> CreateBackupAsync(ServerFactory factory)
    {
        factory.StartServer();
        var path = Path.Combine(_directory, $"backup-{Guid.NewGuid():N}.zip");
        await using (var scope = factory.Services.CreateAsyncScope())
        await using (var target = File.Create(path))
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync(target, BackupPassword, "admin", CancellationToken.None);
            Assert.True(outcome.Succeeded, outcome.Error);
        }

        return path;
    }

    private (string DatabasePath, string AnchorPath) Targets()
    {
        var target = Path.Combine(_directory, $"restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        return (Path.Combine(target, "server.db"), Path.Combine(target, "trust-anchor.key"));
    }

    private static Task<Outcome> RestoreAsync(string archive, string password, string databasePath, string anchorPath) =>
        BackupService.RestoreAsync(archive, password, databasePath, anchorPath, CancellationToken.None);

    private static async Task DamageAsync(string archive, string damage)
    {
        if (damage == "not a zip")
        {
            await File.WriteAllTextAsync(archive, "this is not a zip archive");
            return;
        }

        using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
        switch (damage)
        {
            case "missing anchor":
                zip.GetEntry(BackupService.AnchorEntry)!.Delete();
                break;
            case "missing database":
                zip.GetEntry(BackupService.DatabaseEntry)!.Delete();
                break;
            case "wrong format":
                Replace(zip, BackupService.ManifestEntry, """{"Format":99,"CreatedAtUtc":"2026-10-19T00:00:00Z","ServerVersion":"x","AnchorPublicKey":"AAAA"}""");
                break;
            case "foreign anchor":
                Replace(zip, BackupService.ManifestEntry, """{"Format":1,"CreatedAtUtc":"2026-10-19T00:00:00Z","ServerVersion":"x","AnchorPublicKey":"AAAA"}""");
                break;
            case "broken manifest":
                Replace(zip, BackupService.ManifestEntry, "{ not json");
                break;
        }
    }

    private static void Replace(ZipArchive zip, string name, string content)
    {
        zip.GetEntry(name)!.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
