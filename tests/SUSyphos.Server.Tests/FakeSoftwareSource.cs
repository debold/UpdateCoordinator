using System.Security.Cryptography;
using System.Text.Json.Nodes;

using SUSyphos.Server.Software;
using SUSyphos.TestSupport;
using SUSyphos.WinGet.Catalog;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Tests;
/// <summary>The community source with a test index and canned manifests; Vendor.Shared1 has no readable manifest.</summary>
internal sealed class FakeSoftwareSource(string directory) : ISoftwareSource, IDisposable
{
    public CommunityIndex? Index { get; private set; }

    public void SetIndex(params TestIndexPackage[] packages) =>
        Index = CommunityIndex.Open(TestCommunityIndex.Create(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db"), packages));

    public void ClearIndex() => Index = null;

    public Task<CommunityIndex> RefreshIndexAsync(CancellationToken cancellationToken) => Task.FromResult(Index!);

    public Task<CachedVersion> ReadManifestAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        if (packageId == "Vendor.Shared1")
        {
            throw new HttpRequestException("404");
        }

        var locale = new JsonObject
        {
            ["PackageLocale"] = "en-US",
            ["PackageName"] = packageId == "7zip.7zip" ? "7-Zip" : "Licensed Tool",
            ["Publisher"] = packageId == "7zip.7zip" ? "Igor Pavlov" : "Vendor",
            ["PackageUrl"] = "https://www.7-zip.org/",
            ["LicenseUrl"] = "http://insecure.example/license",
            ["ShortDescription"] = "File archiver",
        };
        if (packageId == "Vendor.Licensed")
        {
            locale["Agreements"] = new JsonArray(new JsonObject { ["AgreementLabel"] = "EULA", ["Agreement"] = "terms" });
        }

        JsonObject[] installers = [new JsonObject { ["Architecture"] = "x64", ["InstallerType"] = "msi", ["InstallerUrl"] = "https://example.com/setup.msi", ["InstallerSha256"] = new string('A', 64), ["ReleaseDate"] = "2026-11-01" }];
        return Task.FromResult(new CachedVersion(packageId, version, locale, installers));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Content of the installer <see cref="CacheInstallersAsync"/> "downloads".</summary>
    public byte[] InstallerContent { get; set; } = "harmless installer"u8.ToArray();

    /// <summary>When set, the download fails with this exception.</summary>
    public Exception? DownloadFailure { get; set; }

    public int Downloads { get; private set; }

    public async Task<CachedVersion> CacheInstallersAsync(PackageCache cache, string packageId, string version, CancellationToken cancellationToken)
    {
        Downloads++;
        if (DownloadFailure is not null)
        {
            throw DownloadFailure;
        }

        var manifest = await ReadManifestAsync(packageId, version, cancellationToken);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(InstallerContent));
        var file = PackageCache.FileName(sha256, new Uri("https://example.com/setup.msi"));
        Directory.CreateDirectory(cache.FilesFolder);
        await File.WriteAllBytesAsync(Path.Combine(cache.FilesFolder, file), InstallerContent, cancellationToken);
        var installer = (JsonObject)manifest.Installers[0].DeepClone();
        installer["InstallerSha256"] = sha256;
        installer["CachedFile"] = file;
        var cached = manifest with { Installers = [installer] };
        cache.Put(cached);
        return cached;
    }}
