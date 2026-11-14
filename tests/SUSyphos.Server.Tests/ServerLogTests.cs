using System.IO.Compression;
using System.Net;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Common.Diagnostics;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Download of the central server's own logs (Marc, 2026-11-14): Admin only, audited, never the database or keys.</summary>
public sealed class ServerLogTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Admin_DownloadsLogsEventsAndTheInstallerCache_WithACollectionFile()
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("admin", Password, AdminRoles.Admin);
        Directory.CreateDirectory(factory.LogDirectory);
        await File.WriteAllTextAsync(Path.Combine(factory.LogDirectory, "server-20991231.log"), "a line of the central server", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(factory.LogDirectory, "notes.txt"), "not a log", TestContext.Current.CancellationToken);
        await factory.WithDbAsync(db =>
        {
            db.Packages.Add(new PackageEntity { PackageId = "7zip.7zip", Name = "7-Zip", AddedBy = "admin" });
            db.Packages.Add(new PackageEntity { PackageId = "Unknown.Never", Name = "Never", AddedBy = "admin" });
            db.PackageVersions.Add(new PackageVersionEntity { PackageId = "7zip.7zip", Version = "26.00", UpdateId = Guid.NewGuid(), InstallerFile = new string('a', 64) + ".msi", InstallerSha256 = new string('a', 64), SizeBytes = 1234, CachedAtUtc = DateTime.UtcNow, ScanState = InstallerScanState.Clean, ScannedAtUtc = DateTime.UtcNow });
            db.PackageVersions.Add(new PackageVersionEntity { PackageId = "Unknown.Never", Version = "1.0", UpdateId = Guid.NewGuid() });
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("admin", Password))
        {
        }

        var page = await (await browser.GetAsync("/administration/susyphos")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains($"href=\"{ServerLogEndpoints.DownloadRoute}\"", page, StringComparison.Ordinal);

        using var response = await browser.GetAsync(ServerLogEndpoints.DownloadRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("central-server-logs-", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(DiagnosticArchive.CollectionFile, names[0]);
        Assert.Contains("server-20991231.log", names);
        Assert.Contains("Events/Application.txt", names);
        Assert.DoesNotContain("notes.txt", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".key", StringComparison.OrdinalIgnoreCase));

        var cache = Read(zip, "Software/installer-cache.txt");
        Assert.Contains("7zip.7zip 26.00", cache, StringComparison.Ordinal);
        Assert.Contains("scan Clean", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown.Never", cache, StringComparison.Ordinal);
        Assert.True(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "CentralServer.LogsDownloaded", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(AdminRoles.Operator)]
    [InlineData(AdminRoles.Reader)]
    public async Task OthersThanAdmins_CannotDownloadTheLogs(string role)
    {
        await using var factory = new ServerFactory();
        await factory.CreateUserAsync("user", Password, role);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("user", Password))
        {
        }

        using var response = await browser.GetAsync(ServerLogEndpoints.DownloadRoute);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == "CentralServer.LogsDownloaded", TestContext.Current.CancellationToken)));
        if (role == AdminRoles.Operator)
        {
            Assert.DoesNotContain(ServerLogEndpoints.DownloadRoute, await (await browser.GetAsync("/administration/susyphos")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithoutSignIn_TheLogsAreRefused()
    {
        await using var factory = new ServerFactory();
        using var browser = new DashboardClient(factory);

        using var response = await browser.GetAsync(ServerLogEndpoints.DownloadRoute);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
