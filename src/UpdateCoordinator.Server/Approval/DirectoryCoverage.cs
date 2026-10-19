using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Approval;

/// <summary>Settings of the AD comparison, bound from the "Coverage" configuration section.</summary>
internal sealed class CoverageOptions
{
    public const string SectionName = "Coverage";

    /// <summary>False turns the AD comparison off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Domain to read; empty uses the domain of the central server.</summary>
    [RegularExpression(ValidationPatterns.DomainName)]
    public string? Domain { get; set; }

    /// <summary>How often Active Directory is read.</summary>
    [Range(typeof(TimeSpan), "00:10:00", "7.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>A server computer account from the last AD comparison.</summary>
internal sealed class DirectoryComputerEntity
{
    public required string Name { get; set; }

    public string? DnsHostName { get; set; }

    public string? OperatingSystem { get; set; }

    /// <summary>Build number from operatingSystemVersion, e.g. 17763; null if unknown.</summary>
    public int? Build { get; set; }

    /// <summary>Windows Server 2016 (build 14393) or newer, the versions this solution supports.</summary>
    public bool Supported { get; set; }

    public DateTime ReadAtUtc { get; set; }
}

/// <summary>Result of the AD comparison for the dashboard.</summary>
internal sealed record CoverageReport(
    DateTime? ReadAtUtc,
    int Supported,
    int Covered,
    IReadOnlyList<DirectoryComputerEntity> Missing,
    IReadOnlyList<DirectoryComputerEntity> Unsupported)
{
    public int? Percent => Supported == 0 ? null : Covered * 100 / Supported;
}

/// <summary>
/// AD comparison (docs/04-central-server.md, section 7): server computer accounts of the domain as target, registered
/// agents as actual state. Only supported Windows Server versions count (Marc, 2026-10-19); older servers are listed
/// separately. A server counts as covered when an approved or pending agent has its DNS name or host name.
/// </summary>
internal sealed partial class DirectoryCoverage(ServerDbContext db, IDirectoryLookup directory, IOptions<CoverageOptions> options, TimeProvider timeProvider)
{
    /// <summary>First build of Windows Server 2016.</summary>
    public const int MinimumSupportedBuild = 14393;

    /// <summary>The domain to read, or null if the central server is not a domain member and none is configured.</summary>
    public string? Domain =>
        options.Value.Domain is { Length: > 0 } configured ? configured
        : IPGlobalProperties.GetIPGlobalProperties().DomainName is { Length: > 0 } own ? own
        : null;

    /// <summary>Reads the server accounts from Active Directory and replaces the stored list. Throws if AD is unreachable.</summary>
    public async Task<int> RefreshAsync(string domain, CancellationToken cancellationToken)
    {
        var computers = await directory.ListServersAsync(domain, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.DirectoryComputers.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.DirectoryComputers.AddRange(computers
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                var build = ParseBuild(c.OperatingSystemVersion);
                return new DirectoryComputerEntity
                {
                    Name = Truncate(c.Name, 64)!,
                    DnsHostName = Truncate(c.DnsHostName, 255),
                    OperatingSystem = Truncate(c.OperatingSystem, 128),
                    Build = build,
                    Supported = build >= MinimumSupportedBuild,
                    ReadAtUtc = now,
                };
            }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return computers.Count;
    }

    public async Task<CoverageReport> GetReportAsync(CancellationToken cancellationToken)
    {
        var computers = await db.DirectoryComputers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.State == RegistrationState.Approved || c.State == RegistrationState.Pending)
            .Select(c => new { c.HostName, c.DomainName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dnsNames = clients.Where(c => c.DomainName is not null).Select(c => $"{c.HostName}.{c.DomainName}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hostNames = clients.Select(c => c.HostName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsCovered(DirectoryComputerEntity computer) =>
            (computer.DnsHostName is not null && dnsNames.Contains(computer.DnsHostName)) || hostNames.Contains(computer.Name);

        var supported = computers.Where(c => c.Supported).ToList();
        var missing = supported.Where(c => !IsCovered(c)).ToList();
        return new CoverageReport(
            computers.Count == 0 ? null : computers.Max(c => c.ReadAtUtc),
            supported.Count,
            supported.Count - missing.Count,
            missing,
            [.. computers.Where(c => !c.Supported)]);
    }

    /// <summary>Build number from an operatingSystemVersion value such as "10.0 (17763)".</summary>
    internal static int? ParseBuild(string? version) =>
        version is not null && BuildRegex().Match(version) is { Success: true } match
        && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var build)
            ? build
            : null;

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"\((\d{1,6})\)")]
    private static partial Regex BuildRegex();
}

/// <summary>Refreshes the AD comparison periodically; failures are logged and retried with the next interval.</summary>
internal sealed partial class CoverageWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<CoverageOptions> options,
    TimeProvider timeProvider,
    ILogger<CoverageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.CurrentValue.Enabled)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var coverage = scope.ServiceProvider.GetRequiredService<DirectoryCoverage>();
                    if (coverage.Domain is { } domain)
                    {
                        var count = await coverage.RefreshAsync(domain, stoppingToken).ConfigureAwait(false);
                        LogRefreshed(count, domain);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogFailed(ex);
                }
            }

            try
            {
                await Task.Delay(options.CurrentValue.Interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AD comparison: {Count} server accounts read from {Domain}.")]
    private partial void LogRefreshed(int count, string domain);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AD comparison failed; retrying with the next interval.")]
    private partial void LogFailed(Exception exception);
}
