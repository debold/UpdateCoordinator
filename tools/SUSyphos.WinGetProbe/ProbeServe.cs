using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

using SUSyphos.WinGet;
using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.WinGetProbe;

/// <summary>The commands cache and serve: a private WinGet REST source with an installer cache, for lab round 4.</summary>
public static class ProbeServe
{
    /// <summary>Where the source answers; files are served below <see cref="FilesPath"/>.</summary>
    public const string ApiPath = "/api/";
    public const string FilesPath = "/files/";

    public static string CacheFolder(ProbeArguments arguments) => arguments.CacheFolder ?? Path.Combine(AppContext.BaseDirectory, "winget-cache");

    /// <summary>Caches one package version; without --version the newest the community source offers.</summary>
    public static async Task<object> CacheAsync(ProbeArguments arguments, Func<WinGetClient> client, ProbeOutput output)
    {
        var id = arguments.Target!;
        var version = arguments.Version;
        if (version is null)
        {
            var found = await client().SearchAsync(id);
            version = found.FirstOrDefault(package => string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase))?.DefaultVersion
                ?? throw new WinGetException($"The community source has no package with the id {id}.");
        }

        var problems = new List<string>();
        var cache = PackageCache.Open(CacheFolder(arguments), problems, verifyFiles: false);
        problems.ForEach(problem => output.Log($"Cache: {problem}"));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SUSyphos-WinGetProbe");
        var cached = await new CommunityManifests(http).CacheAsync(cache, id, version, output.Log);
        output.Log($"Cached {cached.PackageIdentifier} {cached.PackageVersion}: {cached.Installers.Count} installers in {cache.Folder}");
        return new { cached.PackageIdentifier, cached.PackageVersion, Installers = cached.Installers.Select(installer => installer.ToJsonString()), cache.Folder };
    }

    /// <summary>Serves the cache until Ctrl+C.</summary>
    public static async Task<object> ServeAsync(ProbeArguments arguments, ProbeOutput output)
    {
        var problems = new List<string>();
        var cache = PackageCache.Open(CacheFolder(arguments), problems, verifyFiles: true);
        problems.ForEach(problem => output.Log($"Cache: {problem} (left out)"));
        if (cache.Versions.Count == 0)
        {
            throw new WinGetException($"The cache {cache.Folder} holds no package; fill it with the command cache first.");
        }

        using var certificate = FindCertificate(arguments.Certificate!);
        var port = arguments.Port ?? ProbeArguments.DefaultPort;
        var host = certificate.GetNameInfo(X509NameType.DnsName, false) is { Length: > 0 } name ? name : Environment.MachineName;
        var baseAddress = new Uri($"https://{host}:{port}");
        var responder = new RestSourceResponder(cache, new Uri(baseAddress, FilesPath));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = RestSourceEndpoints.MaxRequestBytes;
            kestrel.ListenAnyIP(port, listen => listen.UseHttps(certificate));
        });
        await using var app = builder.Build();
        Map(app, responder, cache, output);

        output.Log($"Serving {cache.Versions.Count} package versions from {cache.Folder} as {new Uri(baseAddress, ApiPath)} (Ctrl+C ends)");
        foreach (var version in cache.Versions)
        {
            output.Log($"  {version.PackageIdentifier} {version.PackageVersion}");
        }

        await app.RunAsync();
        return new { Source = new Uri(baseAddress, ApiPath), Packages = cache.Versions.Select(version => $"{version.PackageIdentifier} {version.PackageVersion}") };
    }

    /// <summary>The routes of the source; public for the tests.</summary>
    public static void Map(WebApplication app, RestSourceResponder responder, PackageCache cache, ProbeOutput output)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(output);
        RestSourceEndpoints.Map(app, ApiPath, FilesPath, _ => responder, () => cache.FilesFolder, output.Log);
    }

    /// <summary>The certificate from LocalMachine\My, else from CurrentUser\My (a developer without administrator rights).</summary>
    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            var certificate = found.FirstOrDefault(candidate => candidate.HasPrivateKey);
            foreach (var other in found.Where(candidate => !ReferenceEquals(candidate, certificate)))
            {
                other.Dispose();
            }

            if (certificate is not null)
            {
                return certificate;
            }
        }

        throw new WinGetException($"No certificate {thumbprint} with private key in LocalMachine\\My or CurrentUser\\My.");
    }
}
