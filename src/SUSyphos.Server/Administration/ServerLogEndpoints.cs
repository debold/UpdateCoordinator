using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Common;
using SUSyphos.Common.Diagnostics;
using SUSyphos.Common.Logging;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Administration;

/// <summary>
/// Download of the central server's own logs for troubleshooting (Marc, 2026-11-14; Admin role, audited): the server
/// logs of the last <see cref="Days"/> days, the event log extract and an overview of the WinGet installer cache, packed
/// by the same <see cref="DiagnosticArchive"/> as the agent logs. Never the database, keys, certificates or passwords.
/// </summary>
internal static partial class ServerLogEndpoints
{
    public const string DownloadRoute = "/administration/server-logs";

    public const int Days = 7;

    public const long MaxBytes = 50L * 1024 * 1024;

    public static IEndpointRouteBuilder MapServerLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DownloadRoute, DownloadAsync)
            .RequireAuthorization(AdminRoles.Policies.Administer)
            .WithName("ServerLogDownload");
        return endpoints;
    }

    /// <summary>The server's log folder: the configured one, or the default below the data folder.</summary>
    public static string LogDirectory(IConfiguration configuration) =>
        configuration[$"{FileLoggingOptions.SectionName}:Directory"] is { Length: > 0 } configured ? configured : Path.Combine(DataPaths.Server, "Logs");

    internal static async Task<IResult> DownloadAsync(
        HttpContext httpContext,
        ServerDbContext db,
        IConfiguration configuration,
        AuditLog audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var software = await SoftwareCacheOverviewAsync(db, cancellationToken).ConfigureAwait(false);
        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-Days);
        var result = new DiagnosticArchive(timeProvider, Days, MaxBytes).Build(
            $"SUSyphos central server logs, version {typeof(ServerLogEndpoints).Assembly.GetName().Version}",
            [
                new LogSource(LogDirectory(configuration), "server-*.log", string.Empty),
                new ArchiveText("Events/Application.txt", () => EventLogExtract.Read(since)),
                new ArchiveText("Software/installer-cache.txt", () => software),
            ]);

        await audit.WriteAsync(httpContext.User.Identity?.Name ?? "unknown", "CentralServer.LogsDownloaded", Environment.MachineName, $"{result.Files} files, {result.Archive.Length} bytes", cancellationToken).ConfigureAwait(false);
        var fileName = string.Create(CultureInfo.InvariantCulture, $"central-server-logs-{SafeName().Replace(Environment.MachineName, "-")}-{timeProvider.GetUtcNow().UtcDateTime:yyyyMMdd-HHmm}.zip");
        return TypedResults.File(result.Archive, "application/zip", fileName);
    }

    /// <summary>Every package version the cache knows: its installer, SHA-256, size, when it was cached and the virus scan.</summary>
    internal static async Task<string> SoftwareCacheOverviewAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        var versions = await db.PackageVersions.AsNoTracking()
            .Where(v => v.InstallerFile != null || v.CachedAtUtc != null || v.ScanState != InstallerScanState.NotScanned || v.ScannedAtUtc != null)
            .Select(v => new { v.PackageId, v.Version, v.InstallerFile, v.InstallerSha256, v.SizeBytes, v.CachedAtUtc, v.ScanState, v.ScanDetail, v.ScannedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var text = new StringBuilder()
            .AppendLine("WinGet installer cache of the central server (package, version, installer file, SHA-256, size, cached, virus scan)")
            .AppendLine();
        foreach (var v in versions.OrderBy(v => v.PackageId, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Version, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{v.PackageId} {v.Version}")
                .AppendLine(CultureInfo.InvariantCulture, $"    file {v.InstallerFile ?? "(not cached)"}, SHA-256 {v.InstallerSha256 ?? "-"}, {(v.SizeBytes is { } size ? size.ToString("N0", CultureInfo.InvariantCulture) + " bytes" : "size unknown")}, cached {Utc(v.CachedAtUtc)}")
                .AppendLine(CultureInfo.InvariantCulture, $"    scan {v.ScanState} at {Utc(v.ScannedAtUtc)}{(v.ScanDetail is { Length: > 0 } detail ? ": " + detail : string.Empty)}");
        }

        text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{versions.Count} versions.");
        return text.ToString();
    }

    private static string Utc(DateTime? value) => value is { } at ? at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "never";

    [GeneratedRegex("[^A-Za-z0-9-]", RegexOptions.None, 1000)]
    private static partial Regex SafeName();
}
