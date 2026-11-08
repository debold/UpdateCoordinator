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
                await DownloadAsync(source, path, sha256, log, cancellationToken);
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

    /// <summary>A download that receives nothing for this long is broken off and tried again.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a download is tried before the command fails.</summary>
    public int Attempts { get; init; } = 3;

    /// <summary>Wait before the second attempt; doubled before each further one.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Interval of the progress lines.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Downloads one installer: retries a connection that stalls or a server error (mirrors of download sites hang now
    /// and then), not a refusal (4xx) and not a file with another SHA-256.
    /// </summary>
    private async Task DownloadAsync(Uri source, string path, string sha256, Action<string> log, CancellationToken cancellationToken)
    {
        var delay = RetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            log(attempt == 1 ? $"Downloading {source}" : $"Downloading {source} (attempt {attempt} of {Attempts})");
            try
            {
                await DownloadOnceAsync(source, path, sha256, log, cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < Attempts && !cancellationToken.IsCancellationRequested && Retryable(exception))
            {
                log($"Download failed: {exception.Message}; trying again in {delay.TotalSeconds:0} s");
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    private static bool Retryable(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status >= 500,
        HttpRequestException or IOException or TimeoutException => true,
        _ => false,
    };

    private async Task DownloadOnceAsync(Uri source, string path, string sha256, Action<string> log, CancellationToken cancellationToken)
    {
        var partial = path + ".part";
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            try
            {
                using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"{source} answered {(int)response.StatusCode}.", null, response.StatusCode);
                }

                var total = response.Content.Headers.ContentLength;
                await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
                await using var target = File.Create(partial);
                var buffer = new byte[81920];
                long received = 0;
                var lastProgress = DateTime.UtcNow;
                int read;
                while ((read = await body.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (DateTime.UtcNow - lastProgress >= ProgressInterval)
                    {
                        lastProgress = DateTime.UtcNow;
                        log(total is > 0 ? $"  {received / 1048576.0:0.0} of {total.Value / 1048576.0:0.0} MB" : $"  {received / 1048576.0:0.0} MB");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{source} sent nothing for {StallTimeout.TotalSeconds:0} s.");
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
