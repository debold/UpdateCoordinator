using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>What one check of the software catalog did.</summary>
internal sealed record SoftwareCheckResult(int MatchedPrograms, int AddedPackages, int NewVersions, int Failures);

/// <summary>
/// The software catalog of the central server (docs/04-central-server.md, section 7c): packages come from the software
/// inventory of the servers (WinGet's match, else the product code in the community index) or from an administrator;
/// new versions come from the community index. Every version becomes an entry of the update catalog, so start rules,
/// rollouts and holds treat it like a Windows update.
/// </summary>
internal sealed partial class SoftwareCatalog(
    ServerDbContext db,
    ISoftwareSource source,
    AuditLog audit,
    IOptions<SoftwareOptions> options,
    TimeProvider timeProvider,
    ILogger<SoftwareCatalog> logger,
    SoftwareLogos? logos = null)
{
    /// <summary>Actor of packages the catalog adds from the inventory.</summary>
    public const string InventoryActor = "inventory";

    private const int MaxManifestLength = 200_000;

    /// <summary>Refreshes the index (when enabled), matches the inventory and looks for new versions.</summary>
    public async Task<SoftwareCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (options.Value.CheckEnabled)
        {
            await source.RefreshIndexAsync(cancellationToken).ConfigureAwait(false);
        }

        var (matched, added, failures) = await MatchInventoryAsync(cancellationToken).ConfigureAwait(false);
        var (versions, versionFailures) = await CheckVersionsAsync(cancellationToken).ConfigureAwait(false);
        return new SoftwareCheckResult(matched, added, versions, failures + versionFailures);
    }

    /// <summary>
    /// Matches installed programs WinGet did not know by the product code (the uninstall key of an MSI) in the index,
    /// then adds every package installed somewhere to the catalog.
    /// </summary>
    public async Task<(int Matched, int Added, int Failures)> MatchInventoryAsync(CancellationToken cancellationToken)
    {
        var matched = 0;
        if (source.Index is { } index)
        {
            var unmatched = await db.ClientSoftware
                .Where(s => s.PackageId == null && EF.Functions.Like(s.KeyName, "{%"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var program in unmatched.Where(p => ProductCodeRegex().IsMatch(p.KeyName)))
            {
                // Only a unique answer counts: a product code shared by several packages says nothing.
                if (index.ByProductCode(program.KeyName) is [var package])
                {
                    program.PackageId = package.Id;
                    program.InstalledVersion = program.DisplayVersion;
                    program.MatchKind = SoftwareMatch.Catalog;
                    matched++;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var known = (await db.Packages.Select(p => p.PackageId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = await db.ClientSoftware.Where(s => s.PackageId != null).Select(s => s.PackageId!).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        int added = 0, failures = 0;
        foreach (var packageId in installed.Where(id => !known.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var outcome = await AddPackageAsync(packageId, InventoryActor, cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                added++;
            }
            else
            {
                failures++;
                LogPackageFailed(packageId, outcome.Error!);
            }
        }

        return (matched, added, failures);
    }

    /// <summary>
    /// Adds a package of the community source to the catalog with its newest version. Adding a package that is there
    /// already succeeds without a change.
    /// </summary>
    public async Task<Outcome> AddPackageAsync(string? packageId, string actor, CancellationToken cancellationToken)
    {
        packageId = packageId?.Trim();
        if (!SoftwarePackages.IsValidId(packageId))
        {
            return new Outcome("The package ID must be a WinGet ID such as 7zip.7zip.");
        }

        if (await db.Packages.AnyAsync(p => p.PackageId == packageId, cancellationToken).ConfigureAwait(false))
        {
            return Outcome.Ok;
        }

        if (source.Index is not { } index)
        {
            return new Outcome("The package index of the WinGet community source has not been loaded yet.");
        }

        if (index.Find(packageId!) is not { } found)
        {
            return new Outcome($"The WinGet community source has no package {packageId}.");
        }

        CachedVersion manifest;
        try
        {
            manifest = await source.ReadManifestAsync(found.Id, found.LatestVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or ArgumentException or JsonException or TaskCanceledException)
        {
            return new Outcome($"The manifest of {found.Id} {found.LatestVersion} could not be read: {exception.Message}");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var package = new PackageEntity { PackageId = found.Id, Name = found.Name, AddedAtUtc = now, AddedBy = actor, CheckedAtUtc = now };
        Describe(package, manifest.DefaultLocale);
        await FetchLogoAsync(package, manifest.DefaultLocale, cancellationToken).ConfigureAwait(false);
        db.Packages.Add(package);
        AddVersion(package, manifest, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Package.Added", found.Id, string.Create(CultureInfo.InvariantCulture, $"{package.Name}, version {found.LatestVersion}{(package.HasAgreements ? ", has agreements" : string.Empty)}"), cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>
    /// An Admin accepts the agreements (licence terms) of a package once (Marc, 2026-11-08); from then on its versions can
    /// start automatically and the agent installs them accepting the agreements. Audited.
    /// </summary>
    public async Task<Outcome> AcceptAgreementsAsync(string packageId, string actor, CancellationToken cancellationToken)
    {
        var package = await db.Packages.SingleOrDefaultAsync(p => p.PackageId == packageId, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            return new Outcome("The package is not in the catalog.");
        }

        if (!package.HasAgreements)
        {
            return new Outcome("The package has no agreements to accept.");
        }

        if (package.AgreementsAcceptedAtUtc is not null)
        {
            return Outcome.Ok;
        }

        package.AgreementsAcceptedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        package.AgreementsAcceptedBy = actor;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Package.AgreementsAccepted", package.PackageId, package.Name, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Adds the newest version of every package when the index names one the catalog does not know yet.</summary>
    public async Task<(int Added, int Failures)> CheckVersionsAsync(CancellationToken cancellationToken)
    {
        if (source.Index is not { } index)
        {
            return (0, 0);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        int added = 0, failures = 0;
        foreach (var package in await db.Packages.ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            package.CheckedAtUtc = now;
            if (index.Find(package.PackageId) is not { } found
                || await db.PackageVersions.AnyAsync(v => v.PackageId == package.PackageId && v.Version == found.LatestVersion, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                var manifest = await source.ReadManifestAsync(found.Id, found.LatestVersion, cancellationToken).ConfigureAwait(false);
                Describe(package, manifest.DefaultLocale);
                await FetchLogoAsync(package, manifest.DefaultLocale, cancellationToken).ConfigureAwait(false);
                AddVersion(package, manifest, now);
                added++;
                LogVersionFound(package.PackageId, found.LatestVersion);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or ArgumentException or JsonException or TaskCanceledException)
            {
                failures++;
                LogPackageFailed(package.PackageId, exception.Message);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (added, failures);
    }

    private Task FetchLogoAsync(PackageEntity package, JsonObject locale, CancellationToken cancellationToken) =>
        logos is null ? Task.CompletedTask : logos.FetchAsync(package, locale, cancellationToken);

    /// <summary>The version and its entry in the update catalog (revision 1, title "name version"). Does not save.</summary>
    private void AddVersion(PackageEntity package, CachedVersion manifest, DateTime now)
    {
        var updateId = SoftwarePackages.SoftwareUpdateId(package.PackageId, manifest.PackageVersion);
        var json = JsonSerializer.Serialize(new JsonObject
        {
            ["PackageIdentifier"] = package.PackageId,
            ["PackageVersion"] = manifest.PackageVersion,
            ["DefaultLocale"] = manifest.DefaultLocale.DeepClone(),
            ["Installers"] = new JsonArray([.. manifest.Installers.Select(i => (JsonNode)i.DeepClone())]),
        });
        db.PackageVersions.Add(new PackageVersionEntity
        {
            PackageId = package.PackageId,
            Version = manifest.PackageVersion,
            UpdateId = updateId,
            ReleaseDate = manifest.Installers.Select(i => DateOnly.TryParse(i["ReleaseDate"]?.ToString(), CultureInfo.InvariantCulture, out var date) ? date : (DateOnly?)null).FirstOrDefault(d => d is not null),
            Manifest = json.Length <= MaxManifestLength ? json : null,
        });
        db.Updates.Add(new UpdateEntity
        {
            UpdateId = updateId,
            Revision = SoftwarePackages.SoftwareRevision,
            Title = Text(SoftwarePackages.Title(package.Name, manifest.PackageVersion), 512)!,
            Kind = UpdateKind.Software,
            PackageId = package.PackageId,
            PackageVersion = manifest.PackageVersion,
            FirstSeenAtUtc = now,
        });
    }

    /// <summary>Takes name, publisher, texts and links from the default locale of the newest manifest.</summary>
    internal static void Describe(PackageEntity package, JsonObject locale)
    {
        package.Name = Text(Value(locale, "PackageName"), PackageEntity.MaxNameLength) ?? package.Name;
        package.Publisher = Text(Value(locale, "Publisher"), PackageEntity.MaxNameLength);
        package.ShortDescription = Text(Value(locale, "ShortDescription"), 512);
        package.Description = Text(Value(locale, "Description"), 10_000, keepLines: true);
        package.Moniker = Text(Value(locale, "Moniker"), 64);
        package.Tags = Text(locale["Tags"] is JsonArray tags ? string.Join(", ", tags.Select(t => t?.ToString()).OfType<string>()) : null, 1024);
        package.License = Text(Value(locale, "License"), 512);
        package.LicenseUrl = Https(Value(locale, "LicenseUrl"));
        package.PackageUrl = Https(Value(locale, "PackageUrl"));
        package.PublisherUrl = Https(Value(locale, "PublisherUrl"));
        package.ReleaseNotesUrl = Https(Value(locale, "ReleaseNotesUrl"));
        var agreements = locale["Agreements"] is JsonArray { Count: > 0 };
        if (agreements && !package.HasAgreements)
        {
            // New agreements need a new acceptance.
            package.AgreementsAcceptedAtUtc = null;
            package.AgreementsAcceptedBy = null;
        }

        package.HasAgreements = agreements;
    }

    private static string? Value(JsonObject locale, string name) => locale[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Text without control characters (line breaks kept where asked), trimmed and cut to the length; null when empty.</summary>
    internal static string? Text(string? value, int maxLength, bool keepLines = false)
    {
        if (value is null)
        {
            return null;
        }

        var cleaned = new string([.. value.Where(c => !char.IsControl(c) || (keepLines && c == '\n'))]).Trim();
        return cleaned.Length == 0 ? null : cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd();
    }

    /// <summary>Only absolute https addresses are kept: the dashboard links them.</summary>
    internal static string? Https(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.AbsoluteUri.Length <= PackageEntity.MaxUrlLength ? uri.AbsoluteUri : null;

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ProductCodeRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Software catalog: new version {Version} of {PackageId}.")]
    private partial void LogVersionFound(string packageId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Software catalog: package {PackageId} could not be added or checked ({Reason}).")]
    private partial void LogPackageFailed(string packageId, string reason);
}

/// <summary>Refreshes the software catalog periodically (<see cref="SoftwareOptions.CheckInterval"/>); a failure waits for the next run.</summary>
internal sealed partial class SoftwareCatalogWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<SoftwareOptions> options,
    TimeProvider timeProvider,
    ILogger<SoftwareCatalogWorker> logger) : BackgroundService
{
    /// <summary>The first check waits a little, so the start of the central server is not slowed down.</summary>
    internal static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = StartDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = options.CurrentValue.CheckInterval;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (!await SoftwareFeature.IsEnabledAsync(scope.ServiceProvider.GetRequiredService<ServerDbContext>(), stoppingToken).ConfigureAwait(false))
                {
                    continue;
                }

                var result = await scope.ServiceProvider.GetRequiredService<SoftwareCatalog>().CheckAsync(stoppingToken).ConfigureAwait(false);
                LogChecked(result.MatchedPrograms, result.AddedPackages, result.NewVersions, result.Failures);
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
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Software catalog checked: {Matched} programs matched by product code, {Added} packages added, {Versions} new versions, {Failures} failures.")]
    private partial void LogChecked(int matched, int added, int versions, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "The software catalog check failed; it runs again later.")]
    private partial void LogFailed(Exception exception);
}
