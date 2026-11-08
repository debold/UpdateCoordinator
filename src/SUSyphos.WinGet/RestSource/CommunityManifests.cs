using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SUSyphos.WinGet.RestSource;

/// <summary>
/// Fills a <see cref="PackageCache"/> from the WinGet community repository (github.com/microsoft/winget-pkgs): reads
/// the manifests of one package version and downloads its installers, keeping only files whose SHA-256 matches the
/// manifest.
/// </summary>
public sealed partial class CommunityManifests(HttpClient http)
{
    /// <summary>Raw files of the community repository.</summary>
    public static readonly Uri Repository = new("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/");

    /// <summary>
    /// Folder of a package version in the repository: <c>manifests/{first letter}/{id with / for .}/{version}/</c>,
    /// e.g. manifests/7/7zip/7zip/26.06/.
    /// </summary>
    /// <exception cref="ArgumentException">Id or version are not of the form WinGet uses.</exception>
    public static string Folder(string id, string version)
    {
        if (!PackageId().IsMatch(id ?? string.Empty))
        {
            throw new ArgumentException($"'{id}' is not a WinGet package id.", nameof(id));
        }

        if (!VersionText().IsMatch(version ?? string.Empty))
        {
            throw new ArgumentException($"'{version}' is not a package version.", nameof(version));
        }

        return $"manifests/{char.ToLowerInvariant(id![0])}/{id.Replace('.', '/')}/{version}/";
    }

    /// <summary>Caches one package version: manifests, then every installer for a server architecture.</summary>
    /// <param name="cache">Cache to fill.</param>
    /// <param name="id">WinGet package id.</param>
    /// <param name="version">Package version.</param>
    /// <param name="log">Receives one line per step.</param>
    /// <param name="cancellationToken">Stops downloads.</param>
    public async Task<CachedVersion> CacheAsync(PackageCache cache, string id, string version, Action<string> log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(log);
        var folder = Folder(id, version);
        var versionManifest = ManifestConverter.Parse(await GetTextAsync(folder + $"{id}.yaml", cancellationToken));
        var locale = versionManifest["DefaultLocale"]?.GetValue<string>() ?? "en-US";
        if (!LocaleText().IsMatch(locale))
        {
            throw new InvalidDataException($"The manifest names an invalid locale '{locale}'.");
        }

        var installerManifest = ManifestConverter.Parse(await GetTextAsync(folder + $"{id}.installer.yaml", cancellationToken));
        var localeManifest = ManifestConverter.Parse(await GetTextAsync(folder + $"{id}.locale.{locale}.yaml", cancellationToken));
        var defaultLocale = ManifestConverter.Locale(localeManifest);
        defaultLocale["PackageLocale"] ??= locale;

        Directory.CreateDirectory(cache.FilesFolder);
        var installers = new List<JsonObject>();
        foreach (var installer in ManifestConverter.Installers(installerManifest))
        {
            var url = installer["InstallerUrl"]?.GetValue<string>();
            var sha256 = installer["InstallerSha256"]?.GetValue<string>();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps || sha256 is null)
            {
                throw new InvalidDataException($"An installer of {id} {version} has no https address or SHA-256.");
            }

            var file = PackageCache.FileName(sha256, source);
            var path = Path.Combine(cache.FilesFolder, file);
            if (File.Exists(path) && string.Equals(PackageCache.Hash(path), sha256, StringComparison.OrdinalIgnoreCase))
            {
                log($"{source} already cached as {file}");
            }
            else
            {
                log($"Downloading {source}");
                await DownloadAsync(source, path, sha256, cancellationToken);
            }

            installer["CachedFile"] = file;
            installers.Add(installer);
        }

        if (installers.Count == 0)
        {
            throw new InvalidDataException($"{id} {version} has no installer for x64, x86 or neutral.");
        }

        var cached = new CachedVersion(id, version, defaultLocale, installers);
        cache.Put(cached);
        return cached;
    }

    private async Task<string> GetTextAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(Repository, path), cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : throw new HttpRequestException($"{new Uri(Repository, path)} answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private async Task DownloadAsync(Uri source, string path, string sha256, CancellationToken cancellationToken)
    {
        var partial = path + ".part";
        try
        {
            using (var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"{source} answered {(int)response.StatusCode}.", null, response.StatusCode);
                }

                await using var target = File.Create(partial);
                await response.Content.CopyToAsync(target, cancellationToken);
            }

            var actual = PackageCache.Hash(partial);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{source} has SHA-256 {actual}, the manifest says {sha256.ToLowerInvariant()}.");
            }

            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}$")]
    private static partial Regex PackageId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{0,63}$")]
    private static partial Regex VersionText();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LocaleText();
}
