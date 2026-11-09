using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using SUSyphos.Server.Data;

namespace SUSyphos.Server.Software;

/// <summary>Reads a logo from the internet. Separate so tests decide what comes back.</summary>
internal interface ILogoDownloader
{
    /// <summary>The bytes at the address, at most <paramref name="maxBytes"/>; null when the address does not answer with them.</summary>
    Task<byte[]?> GetAsync(Uri address, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Logos of the packages (concept 2026-11-08): the icon of the manifest, else the website symbol (favicon) of the product
/// or publisher page. The central server fetches each logo once, keeps only PNG and ICO files up to 256 KB (no SVG, which
/// can carry script) under a name of their SHA-256 and serves them itself, so the browser loads nothing from other sites
/// and the Content Security Policy stays <c>img-src 'self'</c>. Switched off with <see cref="SoftwareOptions.Logos"/>.
/// </summary>
internal sealed partial class SoftwareLogos(ILogoDownloader downloader, IOptions<SoftwareOptions> options, ILogger<SoftwareLogos> logger)
{
    public const int MaxBytes = 256 * 1024;

    /// <summary>Path below which the dashboard serves the logos.</summary>
    public const string Route = "/software-logos/";

    public string Folder => Path.Combine(options.Value.Directory, "logos");

    /// <summary>Fetches a logo for the package if it has none yet; failures leave it without one (the dashboard shows initials).</summary>
    public async Task FetchAsync(PackageEntity package, JsonObject? locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!options.Value.Logos || package.LogoFile is not null)
        {
            return;
        }

        foreach (var address in Candidates(package, locale))
        {
            try
            {
                if (await downloader.GetAsync(address, MaxBytes, cancellationToken).ConfigureAwait(false) is { } data && Extension(data) is { } extension)
                {
                    var name = Convert.ToHexStringLower(SHA256.HashData(data)) + extension;
                    Directory.CreateDirectory(Folder);
                    var path = Path.Combine(Folder, name);
                    if (!File.Exists(path))
                    {
                        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
                    }

                    package.LogoFile = name;
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or SocketException)
            {
                LogFailed(package.PackageId, address, exception.Message);
            }
        }
    }

    /// <summary>The icons of the manifest (largest first), then the website symbols of product and publisher page; https only.</summary>
    internal static IEnumerable<Uri> Candidates(PackageEntity package, JsonObject? locale)
    {
        var icons = (locale?["Icons"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(i => i["IconFileType"]?.ToString() is "png" or "ico")
            .OrderByDescending(i => i["IconResolution"]?.ToString() is { } r && int.TryParse(r.Split('x')[0], out var size) ? size : 0)
            .Select(i => i["IconUrl"]?.ToString());
        var favicons = new[] { package.PackageUrl, package.PublisherUrl }
            .Select(page => Uri.TryCreate(page, UriKind.Absolute, out var uri) ? new Uri(uri, "/favicon.ico").AbsoluteUri : null);
        return icons.Concat(favicons)
            .Select(text => Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort ? uri : null)
            .OfType<Uri>()
            .DistinctBy(uri => uri.AbsoluteUri);
    }

    /// <summary>".png" or ".ico" when the bytes are such an image; null for anything else.</summary>
    internal static string? Extension(ReadOnlySpan<byte> data) =>
        data.Length > 8 && data[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? ".png"
        : data.Length > 6 && data[..4].SequenceEqual((byte[])[0x00, 0x00, 0x01, 0x00]) ? ".ico"
        : null;

    /// <summary>True for names this class gives logo files; anything else is never served.</summary>
    public static bool IsLogoFileName(string name) => LogoNameRegex().IsMatch(name);

    [GeneratedRegex("^[0-9a-f]{64}\\.(png|ico)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LogoNameRegex();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Logo of {PackageId} not loaded from {Address}: {Reason}.")]
    private partial void LogFailed(string packageId, Uri address, string reason);
}

/// <summary>
/// Downloads logos over HTTPS with a size limit, without following redirects to other schemes, and never from addresses
/// inside the own network: the addresses come from public manifests, but must not turn the central server into a probe
/// of the internal network.
/// </summary>
internal sealed class HttpLogoDownloader(HttpClient http) : ILogoDownloader
{
    public async Task<byte[]?> GetAsync(Uri address, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme != Uri.UriSchemeHttps || !await IsPublicAsync(address.DnsSafeHost, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>False for IP literals and names that resolve to loopback, private, link-local or unique-local addresses.</summary>
    internal static async Task<bool> IsPublicAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out _))
        {
            return false;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily != AddressFamily.InterNetwork || !(bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            || bytes[0] == 0
            || bytes[0] >= 224);
    }
}
