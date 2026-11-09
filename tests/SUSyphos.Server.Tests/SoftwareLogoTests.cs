using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Software;
using SUSyphos.TestSupport;

namespace SUSyphos.Server.Tests;

/// <summary>Logos of the packages: fetched once, only PNG and ICO up to 256 KB, served by the central server itself.</summary>
public sealed class SoftwareLogoTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Ico = [0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10];
    private static readonly byte[] Svg = "<svg onload=\"alert(1)\"/>"u8.ToArray();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", "logos-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SoftwareLogos Logos(FakeDownloader downloader, bool enabled = true) =>
        new(downloader, Options.Create(new SoftwareOptions { Directory = _directory, Logos = enabled }), NullLogger<SoftwareLogos>.Instance);

    private static PackageEntity Package() => new() { PackageId = "7zip.7zip", Name = "7-Zip", AddedBy = "admin", PackageUrl = "https://www.7-zip.org/download.html", PublisherUrl = "https://igor.example/" };

    private static JsonObject Locale(params (string Url, string Type, string Resolution)[] icons) => new()
    {
        ["Icons"] = new JsonArray([.. icons.Select(i => (JsonNode)new JsonObject { ["IconUrl"] = i.Url, ["IconFileType"] = i.Type, ["IconResolution"] = i.Resolution })]),
    };

    [Fact]
    public void Candidates_PreferTheLargestManifestIcon_ThenFavicons_HttpsOnly()
    {
        var candidates = SoftwareLogos.Candidates(Package(), Locale(("https://cdn.example/16.png", "png", "16x16"), ("https://cdn.example/64.png", "png", "64x64"), ("https://cdn.example/a.svg", "svg", "custom"), ("http://cdn.example/x.png", "png", "256x256"))).Select(u => u.AbsoluteUri).ToList();

        Assert.Equal(["https://cdn.example/64.png", "https://cdn.example/16.png", "https://www.7-zip.org/favicon.ico", "https://igor.example/favicon.ico"], candidates);
    }

    [Fact]
    public async Task Logo_IsStoredUnderItsHash_AndSetOnThePackage()
    {
        var package = Package();
        var downloader = new FakeDownloader { ["https://www.7-zip.org/favicon.ico"] = Ico };

        await Logos(downloader).FetchAsync(package, null, CancellationToken.None);

        Assert.NotNull(package.LogoFile);
        Assert.EndsWith(".ico", package.LogoFile, StringComparison.Ordinal);
        Assert.True(SoftwareLogos.IsLogoFileName(package.LogoFile));
        Assert.Equal(Ico, await File.ReadAllBytesAsync(Path.Combine(_directory, "logos", package.LogoFile), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OtherContent_IsNeverStored_AndTheNextCandidateIsTried()
    {
        var package = Package();
        var downloader = new FakeDownloader { ["https://cdn.example/icon.png"] = Svg, ["https://www.7-zip.org/favicon.ico"] = "<html>"u8.ToArray(), ["https://igor.example/favicon.ico"] = Png };

        await Logos(downloader).FetchAsync(package, Locale(("https://cdn.example/icon.png", "png", "32x32")), CancellationToken.None);

        Assert.EndsWith(".png", package.LogoFile, StringComparison.Ordinal);
        Assert.Equal(3, downloader.Requests.Count);
    }

    [Fact]
    public async Task SwitchedOff_OrAlreadyThere_NothingIsFetched()
    {
        var downloader = new FakeDownloader { ["https://www.7-zip.org/favicon.ico"] = Ico };
        var package = Package();
        await Logos(downloader, enabled: false).FetchAsync(package, null, CancellationToken.None);
        var existing = Package();
        existing.LogoFile = new string('a', 64) + ".png";
        await Logos(downloader).FetchAsync(existing, null, CancellationToken.None);

        Assert.Null(package.LogoFile);
        Assert.Empty(downloader.Requests);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1::1", true)]
    public void InternalAddresses_AreNeverFetched(string address, bool isPublic)
    {
        Assert.Equal(isPublic, HttpLogoDownloader.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Downloader_RefusesLiteralAndLocalHosts(string host)
    {
        using var http = new HttpClient();

        Assert.Null(await new HttpLogoDownloader(http).GetAsync(new Uri($"https://{host}/favicon.ico"), SoftwareLogos.MaxBytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LogoRoute_ServesCachedLogosToSignedInReadersOnly()
    {
        await using var factory = new ServerFactory();
        factory.StartServer();
        var folder = factory.Services.GetRequiredService<IOptions<SoftwareOptions>>().Value.Directory;
        var name = new string('b', 64) + ".png";
        Directory.CreateDirectory(Path.Combine(folder, "logos"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "logos", name), Png, TestContext.Current.CancellationToken);
        await factory.CreateUserAsync("reader", "correct horse battery", AdminRoles.Reader);
        using var browser = new DashboardClient(factory);
        using (await browser.LoginAsync("reader", "correct horse battery"))
        {
        }

        using var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var refused = await anonymous.GetAsync(new Uri(SoftwareLogos.Route + name, UriKind.Relative), TestContext.Current.CancellationToken);
        using var served = await browser.GetAsync(SoftwareLogos.Route + name);
        using var invalid = await browser.GetAsync(SoftwareLogos.Route + "..%2Fserver.db");

        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
    }

    private sealed class FakeDownloader : Dictionary<string, byte[]>, ILogoDownloader
    {
        public List<Uri> Requests { get; } = [];

        public Task<byte[]?> GetAsync(Uri address, int maxBytes, CancellationToken cancellationToken)
        {
            Requests.Add(address);
            return Task.FromResult(TryGetValue(address.AbsoluteUri, out var data) && data.Length <= maxBytes ? data : null);
        }
    }
}
