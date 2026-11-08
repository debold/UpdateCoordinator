using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

using SUSyphos.Common;
using SUSyphos.WinGet.Catalog;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>Settings of the software catalog (section <c>Software</c>).</summary>
internal sealed class SoftwareOptions
{
    public const string SectionName = "Software";

    /// <summary>Folder for the package index of the community source and, later, the installer cache.</summary>
    public string Directory { get; set; } = Path.Combine(DataPaths.Server, "Software");

    /// <summary>Whether the central server reads the community source; off for environments without internet.</summary>
    public bool CheckEnabled { get; set; } = true;

    /// <summary>How often the index is refreshed and the packages are checked for new versions.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Address of the package index (source2.msix); the community source by default.</summary>
    public Uri IndexAddress { get; set; } = CommunityIndex.SourceAddress;
}

/// <summary>
/// The WinGet community source as the central server uses it: the package index (refreshed with
/// <see cref="RefreshIndexAsync"/>) and the manifests of single versions. Separate so tests decide what the source holds.
/// </summary>
internal interface ISoftwareSource
{
    /// <summary>The last index that was loaded, or null when none is there yet.</summary>
    CommunityIndex? Index { get; }

    /// <summary>Downloads the index anew; the previous one stays when the download fails.</summary>
    Task<CommunityIndex> RefreshIndexAsync(CancellationToken cancellationToken);

    /// <summary>The manifests of one version (names, links, licence, agreements, installers), without an installer.</summary>
    Task<CachedVersion> ReadManifestAsync(string packageId, string version, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the community source over HTTPS: the index from the Microsoft CDN, the manifests from github.com/microsoft/winget-pkgs.
/// The index lives as a file in <see cref="SoftwareOptions.Directory"/>, so it survives a restart.
/// </summary>
internal sealed partial class CommunitySoftwareSource(HttpClient http, IOptions<SoftwareOptions> options, ILogger<CommunitySoftwareSource> logger) : ISoftwareSource
{
    private const string PackageFileName = "source2.msix";
    private const string DatabaseFileName = "index.db";

    private readonly Lock _gate = new();
    private CommunityIndex? _index;
    private bool _opened;

    public CommunityIndex? Index
    {
        get
        {
            lock (_gate)
            {
                if (!_opened)
                {
                    _opened = true;
                    var path = Path.Combine(options.Value.Directory, DatabaseFileName);
                    if (File.Exists(path))
                    {
                        try
                        {
                            _index = CommunityIndex.Open(path);
                        }
                        catch (InvalidDataException exception)
                        {
                            LogIndexUnreadable(exception.Message);
                        }
                    }
                }

                return _index;
            }
        }
    }

    public async Task<CommunityIndex> RefreshIndexAsync(CancellationToken cancellationToken)
    {
        var directory = options.Value.Directory;
        Directory.CreateDirectory(directory);
        var package = Path.Combine(directory, PackageFileName);
        using (var response = await http.GetAsync(options.Value.IndexAddress, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > CommunityIndex.MaxPackageBytes)
            {
                throw new InvalidDataException("The package index is larger than expected.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(package);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > CommunityIndex.MaxPackageBytes)
                {
                    throw new InvalidDataException("The package index is larger than expected.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        CommunityIndex index;
        lock (_gate)
        {
            index = CommunityIndex.Extract(package, Path.Combine(directory, DatabaseFileName));
            _index = index;
            _opened = true;
        }

        File.Delete(package);
        LogIndexRefreshed(index.Count, index.LastWrite);
        return index;
    }

    public Task<CachedVersion> ReadManifestAsync(string packageId, string version, CancellationToken cancellationToken) =>
        new CommunityManifests(http).ReadAsync(packageId, version, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "WinGet package index refreshed: {Count} packages, written by Microsoft {LastWrite}.")]
    private partial void LogIndexRefreshed(int count, DateTimeOffset? lastWrite);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored WinGet package index cannot be read ({Reason}); it is downloaded again with the next check.")]
    private partial void LogIndexUnreadable(string reason);
}
