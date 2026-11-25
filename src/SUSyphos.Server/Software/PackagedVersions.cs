using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>
/// The versions of the MSIX packages inside a cached installer, and the translation of the version Windows reports for a
/// packaged app into the WinGet version of the package. A packaged app carries its own version, which can differ from the
/// WinGet version (lab, 2026-11-25: PowerShell 7.7.0.0 is the package 2026.1110.418.0); compared directly, the installed
/// app looked newer than every version of the source and was never updated.
/// </summary>
internal static class PackagedVersions
{
    /// <summary>Longest list stored per version (column length).</summary>
    public const int MaxLength = 256;

    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>
    /// True when the manifest of a version has MSIX or APPX installers. Only these are read: the cache keeps no telling
    /// extension for bundles (".msixbundle" is stored as ".bin").
    /// </summary>
    public static bool HasPackages(string? manifestJson) =>
        manifestJson is not null
        && (manifestJson.Contains("\"msix\"", StringComparison.OrdinalIgnoreCase) || manifestJson.Contains("\"appx\"", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The identity versions of a package file: of the bundle and of every package it lists, or of the single package.
    /// Empty for any other file and for a file that is no readable package.
    /// </summary>
    public static IReadOnlyList<string> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (Document(archive, "AppxMetadata/AppxBundleManifest.xml") is { } bundle)
            {
                return Versions(bundle.Root?.Elements().Where(e => e.Name.LocalName == "Identity")
                    .Concat(bundle.Descendants().Where(e => e.Name.LocalName == "Package" && e.Parent?.Name.LocalName == "Packages")));
            }

            return Document(archive, "AppxManifest.xml") is { } package
                ? Versions(package.Root?.Elements().Where(e => e.Name.LocalName == "Identity"))
                : [];
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The stored form: distinct versions, space separated; null when there are none.</summary>
    public static string? Join(IEnumerable<string> versions)
    {
        var text = string.Join(' ', versions.Distinct(StringComparer.OrdinalIgnoreCase));
        return text.Length == 0 || text.Length > MaxLength ? null : text;
    }

    /// <summary>
    /// The WinGet version whose installer holds the reported package version, else the reported version unchanged.
    /// <paramref name="versions"/> are the known versions of the package with their stored package versions.
    /// </summary>
    public static string? ToPackageVersion(IEnumerable<(string Version, string? PackagedVersions)> versions, string? reported)
    {
        if (reported is null)
        {
            return null;
        }

        foreach (var (version, packaged) in versions)
        {
            if (packaged is not null && packaged.Split(' ').Contains(reported, StringComparer.OrdinalIgnoreCase))
            {
                return version;
            }
        }

        return reported;
    }

    /// <summary>
    /// Sets the installed version of the packaged apps the central server matched itself (by package family name) from
    /// their reported version; returns how many rows changed. WinGet's own matches keep WinGet's version.
    /// </summary>
    public static async Task<int> RemapAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.ClientSoftware
            .Where(s => s.View == SoftwareView.Msix && s.MatchKind == SoftwareMatch.Catalog && s.PackageId != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return 0;
        }

        var map = await LoadAsync(db, rows.Select(r => r.PackageId!), cancellationToken).ConfigureAwait(false);
        var changed = 0;
        foreach (var row in rows)
        {
            var version = map(row.PackageId!, row.DisplayVersion);
            if (!string.Equals(version, row.InstalledVersion, StringComparison.Ordinal))
            {
                row.InstalledVersion = version;
                changed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>The translation for these packages, loaded once: package id and reported version to the WinGet version.</summary>
    public static async Task<Func<string, string?, string?>> LoadAsync(ServerDbContext db, IEnumerable<string> packageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ids = packageIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = ids.Count == 0 ? [] : await db.PackageVersions.AsNoTracking()
            .Where(v => ids.Contains(v.PackageId) && v.PackagedVersions != null)
            .Select(v => new { v.PackageId, v.Version, v.PackagedVersions })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byPackage = known
            .ToLookup(v => v.PackageId, v => (v.Version, v.PackagedVersions), StringComparer.OrdinalIgnoreCase);
        return (packageId, reported) => ToPackageVersion(byPackage[packageId].OrderByDescending(v => v.Version, VersionComparer.Instance), reported);
    }

    private static XDocument? Document(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null || entry.Length > MaxManifestBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxManifestBytes });
        return XDocument.Load(reader);
    }

    private static string[] Versions(IEnumerable<XElement>? elements) =>
        [.. (elements ?? []).Select(e => (string?)e.Attribute("Version")).OfType<string>().Where(v => System.Version.TryParse(v, out _)).Distinct(StringComparer.OrdinalIgnoreCase)];
}
