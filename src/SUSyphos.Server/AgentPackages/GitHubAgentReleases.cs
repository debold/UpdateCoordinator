using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

namespace SUSyphos.Server.AgentPackages;

/// <summary>
/// A release of the project offered by the release source: version, agent MSI, checksum file and, when published, the
/// central server MSI, the release page and its notes (Markdown, as written on GitHub).
/// </summary>
internal sealed record AgentRelease(AgentVersion Version, Uri Package, Uri Checksums, bool Prerelease, Uri? ServerPackage = null, Uri? Page = null, string? Notes = null);

/// <summary>Where new agent versions come from; replaced by a fake in tests.</summary>
internal interface IAgentReleaseSource
{
    Task<IReadOnlyList<AgentRelease>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Opens a release asset as a stream; the caller disposes it.</summary>
    Task<Stream> OpenAsync(Uri asset, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the releases of the project on GitHub (tags release/vX.Y.Z, assets SUSyphos.Agent-X.Y.Z.msi,
/// SUSyphos.Server-X.Y.Z.msi and SHA256SUMS.txt). Only assets and pages of the configured repository are accepted.
/// </summary>
internal sealed partial class GitHubAgentReleases(HttpClient http, IOptions<AgentPackageOptions> options) : IAgentReleaseSource
{
    public async Task<IReadOnlyList<AgentRelease>> ListAsync(CancellationToken cancellationToken)
    {
        var repository = options.Value.Repository;
        var releases = await http.GetFromJsonAsync<List<GitHubRelease>>(
            new Uri($"https://api.github.com/repos/{repository}/releases?per_page=30"), cancellationToken).ConfigureAwait(false) ?? [];
        return Parse(releases, repository);
    }

    /// <summary>Turns the GitHub release list into releases; anything not matching the expected tags, names and hosts is skipped.</summary>
    internal static IReadOnlyList<AgentRelease> Parse(IEnumerable<GitHubRelease> releases, string repository)
    {
        var result = new List<AgentRelease>();
        foreach (var release in releases.Where(r => !r.Draft))
        {
            var tag = TagRegex().Match(release.TagName ?? string.Empty);
            if (!tag.Success || !AgentVersion.TryParse(tag.Groups["version"].Value, out var version))
            {
                continue;
            }

            var package = release.Assets?.FirstOrDefault(a => a.Name == AgentPackageStore.FileNameFor(version));
            var checksums = release.Assets?.FirstOrDefault(a => a.Name == "SHA256SUMS.txt");
            var server = release.Assets?.FirstOrDefault(a => a.Name == ServerFileNameFor(version));
            if (package?.Url is { } packageUrl && checksums?.Url is { } checksumUrl && IsRepositoryAsset(packageUrl, repository) && IsRepositoryAsset(checksumUrl, repository))
            {
                result.Add(new AgentRelease(
                    version,
                    packageUrl,
                    checksumUrl,
                    release.Prerelease,
                    server?.Url is { } serverUrl && IsRepositoryAsset(serverUrl, repository) ? serverUrl : null,
                    release.Page is { } page && IsReleasePage(page, repository) ? page : null,
                    ReleaseNotes.Limit(release.Body)));
            }
        }

        return result;
    }

    public Task<Stream> OpenAsync(Uri asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!IsRepositoryAsset(asset, options.Value.Repository))
        {
            throw new ArgumentException("Only release assets of the configured repository are downloaded.", nameof(asset));
        }

        return http.GetStreamAsync(asset, cancellationToken);
    }

    /// <summary>https://github.com/{repository}/releases/download/… only; GitHub redirects from there to its storage over HTTPS.</summary>
    internal static bool IsRepositoryAsset(Uri url, string repository) =>
        url.IsAbsoluteUri
        && url.Scheme == Uri.UriSchemeHttps
        && url.IsDefaultPort
        && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && url.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>https://github.com/{repository}/releases/tag/... only: the release page the dashboard links to.</summary>
    internal static bool IsReleasePage(Uri url, string repository) =>
        url.IsAbsoluteUri
        && url.Scheme == Uri.UriSchemeHttps
        && url.IsDefaultPort
        && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && url.AbsolutePath.StartsWith($"/{repository}/releases/tag/", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(url.Query)
        && string.IsNullOrEmpty(url.Fragment);

    /// <summary>File name of the central server MSI in a release.</summary>
    internal static string ServerFileNameFor(AgentVersion version) => $"SUSyphos.Server-{version}.msi";

    [GeneratedRegex(@"^release/v(?<version>[0-9A-Za-z.+-]{5,40})$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TagRegex();

    internal sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets,
        [property: JsonPropertyName("html_url")] Uri? Page = null,
        [property: JsonPropertyName("body")] string? Body = null);

    internal sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] Uri? Url);
}
