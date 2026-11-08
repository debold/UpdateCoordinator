using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.WinGet.Tests;

public sealed class RestSourceTests : IDisposable
{
    private const string InstallerYaml =
        """
        PackageIdentifier: 7zip.7zip
        PackageVersion: "26.06"
        Scope: machine
        UpgradeBehavior: install
        Commands:
        - 7z
        InstallerSuccessCodes:
        - 3010
        InstallerSwitches:
          Silent: /S
          Log: /log
        Installers:
        - Architecture: x64
          InstallerType: wix
          InstallerUrl: https://example.org/7z2606-x64.msi
          InstallerSha256: AAAA
          InstallerSwitches:
            InstallLocation: INSTALLDIR="<INSTALLPATH>"
            Silent: /quiet
          ProductCode: '{23170F69-40C1-2702-2606-000001000000}'
          AppsAndFeaturesEntries:
          - UpgradeCode: '{23170F69-40C1-2702-0000-000004000000}'
        - Architecture: arm64
          InstallerType: exe
          InstallerUrl: https://example.org/7z2606-arm64.exe
          InstallerSha256: BBBB
        - Architecture: x86
          InstallerType: exe
          Scope: user
          InstallerUrl: https://example.org/7z2606.exe
          InstallerSha256: CCCC
          RequireExplicitUpgrade: true
          ReleaseDate: 2026-11-07
        ManifestType: installer
        ManifestVersion: 1.28.0
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("susyphos-source-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Installers_CopiesSharedFieldsAndKeepsOnlyX64WhenThereIsOne()
    {
        var installers = ManifestConverter.Installers(ManifestConverter.Parse(InstallerYaml));

        var msi = Assert.Single(installers);
        Assert.Equal("x64", msi["Architecture"]!.GetValue<string>());
        Assert.Equal("machine", msi["Scope"]!.GetValue<string>());
        Assert.Equal("/quiet", msi["InstallerSwitches"]!["Silent"]!.GetValue<string>());
        Assert.Equal("/log", msi["InstallerSwitches"]!["Log"]!.GetValue<string>());
        Assert.Equal(3010, msi["InstallerSuccessCodes"]![0]!.GetValue<long>());
        Assert.Equal("7z", msi["Commands"]![0]!.GetValue<string>());
        Assert.Null(msi["PackageIdentifier"]);
        Assert.Null(msi["ManifestType"]);
    }

    [Fact]
    public void Installers_FallsBackToX86WithoutX64()
    {
        var withoutX64 = ManifestConverter.Parse(InstallerYaml);
        withoutX64["Installers"]!.AsArray().RemoveAt(0);

        var exe = Assert.Single(ManifestConverter.Installers(withoutX64));

        Assert.Equal("x86", exe["Architecture"]!.GetValue<string>());
        Assert.Equal("user", exe["Scope"]!.GetValue<string>());
        Assert.True(exe["RequireExplicitUpgrade"]!.GetValue<bool>());
        Assert.Equal("2026-11-07", exe["ReleaseDate"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_KeepsVersionsAndQuotedValuesAsText()
    {
        var manifest = ManifestConverter.Parse("PackageVersion: 26.06\nOther: \"true\"\nNumber: 12\nInstallerReturnCode: '5'\n");

        Assert.Equal("26.06", manifest["PackageVersion"]!.GetValue<string>());
        Assert.Equal("true", manifest["Other"]!.GetValue<string>());
        Assert.Equal("12", manifest["Number"]!.GetValue<string>());
        Assert.Equal("5", manifest["InstallerReturnCode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("- just\n- a list\n")]
    [InlineData("key: [unclosed\n")]
    [InlineData("")]
    public void Parse_RejectsWhatIsNoManifest(string yaml)
    {
        Assert.Throws<InvalidDataException>(() => ManifestConverter.Parse(yaml));
    }

    [Fact]
    public void Installers_NeedsAnInstallerList()
    {
        Assert.Throws<InvalidDataException>(() => ManifestConverter.Installers(ManifestConverter.Parse("PackageIdentifier: a.b\n")));
    }

    [Fact]
    public void Locale_DropsTheFileFields()
    {
        var locale = ManifestConverter.Locale(ManifestConverter.Parse("PackageIdentifier: a.b\nPackageVersion: '1'\nPackageLocale: en-US\nPackageName: Tool\nManifestType: defaultLocale\nManifestVersion: 1.28.0\n"));

        Assert.Equal(["PackageLocale", "PackageName"], locale.Select(field => field.Key));
    }

    [Theory]
    [InlineData("7zip.7zip", "26.06", "manifests/7/7zip/7zip/26.06/")]
    [InlineData("Microsoft.VCRedist.2015+.x64", "14.51.36247.0", "manifests/m/Microsoft/VCRedist/2015+/x64/14.51.36247.0/")]
    [InlineData("Notepad++.Notepad++", "8.8.1", "manifests/n/Notepad++/Notepad++/8.8.1/")]
    public void Folder_FollowsTheRepositoryLayout(string id, string version, string expected)
    {
        Assert.Equal(expected, CommunityManifests.Folder(id, version));
    }

    [Theory]
    [InlineData("../evil", "1.0")]
    [InlineData("a.b", "../1.0")]
    [InlineData("a.b", "1.0/../../x")]
    [InlineData("", "1.0")]
    [InlineData("a.b", "")]
    public void Folder_RejectsPathsOutOfTheRepository(string id, string version)
    {
        Assert.Throws<ArgumentException>(() => CommunityManifests.Folder(id, version));
    }

    [Fact]
    public void FileName_IsTheHashWithASafeExtension()
    {
        var hash = new string('A', 64);

        Assert.Equal(new string('a', 64) + ".msi", PackageCache.FileName(hash, new Uri("https://example.org/x/7z.MSI")));
        Assert.Equal(new string('a', 64) + ".bin", PackageCache.FileName(hash, new Uri("https://example.org/download?id=3")));
        Assert.Equal(new string('a', 64) + ".bin", PackageCache.FileName(hash, new Uri("https://example.org/a.verylongextension")));
        Assert.Throws<ArgumentException>(() => PackageCache.FileName("abc", new Uri("https://example.org/a.msi")));
    }

    [Theory]
    [InlineData("index.json", false)]
    [InlineData("../index.json", false)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000.msi", true)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000.MSI", false)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000.msi/x", false)]
    public void IsCacheFileName_AcceptsOnlyCacheFiles(string name, bool expected)
    {
        Assert.Equal(expected, PackageCache.IsCacheFileName(name));
    }

    [Fact]
    public async Task CacheAsync_StoresManifestAndVerifiedInstaller()
    {
        var installer = Encoding.ASCII.GetBytes("installer bytes");
        var hash = Convert.ToHexString(SHA256.HashData(installer));
        using var http = new HttpClient(new FakeHandler(Responses(hash, installer)));
        var cache = PackageCache.Open(_folder, [], verifyFiles: false);
        var log = new List<string>();

        var cached = await new CommunityManifests(http).CacheAsync(cache, "Vendor.Tool", "1.2", log.Add, TestContext.Current.CancellationToken);

        Assert.Equal("Tool", cached.DefaultLocale["PackageName"]!.GetValue<string>());
        var file = Assert.Single(cached.Installers)["CachedFile"]!.GetValue<string>();
        Assert.Equal(hash.ToLowerInvariant() + ".msi", file);
        Assert.Equal(installer, File.ReadAllBytes(Path.Combine(cache.FilesFolder, file)));
        Assert.Single(PackageCache.Open(_folder, [], verifyFiles: true).Versions);

        // A second run finds the file and does not download it again.
        await new CommunityManifests(http).CacheAsync(cache, "Vendor.Tool", "1.2", log.Add, TestContext.Current.CancellationToken);
        Assert.Contains(log, line => line.Contains("already cached", StringComparison.Ordinal));
        Assert.Single(PackageCache.Open(_folder, [], verifyFiles: true).Versions);
    }

    [Fact]
    public async Task CacheAsync_TriesAStalledDownloadAgain()
    {
        var installer = Encoding.ASCII.GetBytes("installer bytes");
        var responses = Responses(Convert.ToHexString(SHA256.HashData(installer)), installer);
        var stalls = 1;
        using var http = new HttpClient(new FakeHandler(responses, url => url.EndsWith(".msi", StringComparison.Ordinal) && stalls-- > 0));
        var log = new List<string>();
        var manifests = new CommunityManifests(http) { StallTimeout = TimeSpan.FromMilliseconds(200), RetryDelay = TimeSpan.Zero };

        await manifests.CacheAsync(PackageCache.Open(_folder, [], false), "Vendor.Tool", "1.2", log.Add, TestContext.Current.CancellationToken);

        Assert.Contains(log, line => line.Contains("sent nothing", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("attempt 2 of 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CacheAsync_GivesUpAfterTheLastAttempt()
    {
        var responses = Responses(new string('0', 64), []);
        using var http = new HttpClient(new FakeHandler(responses, url => url.EndsWith(".msi", StringComparison.Ordinal)));
        var manifests = new CommunityManifests(http) { StallTimeout = TimeSpan.FromMilliseconds(100), RetryDelay = TimeSpan.Zero, Attempts = 2 };

        await Assert.ThrowsAsync<TimeoutException>(() => manifests.CacheAsync(PackageCache.Open(_folder, [], false), "Vendor.Tool", "1.2", _ => { }, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_folder, PackageCache.FilesFolderName)));
    }

    [Fact]
    public async Task CacheAsync_DoesNotRetryARefusal()
    {
        var responses = Responses(new string('0', 64), []);
        responses.Remove("https://example.org/tool.msi");
        using var http = new HttpClient(new FakeHandler(responses));
        var log = new List<string>();
        var manifests = new CommunityManifests(http) { RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAsync<HttpRequestException>(() => manifests.CacheAsync(PackageCache.Open(_folder, [], false), "Vendor.Tool", "1.2", log.Add, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(log, line => line.Contains("attempt 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CacheAsync_RejectsAnInstallerWithAnotherHash()
    {
        using var http = new HttpClient(new FakeHandler(Responses(new string('0', 64), Encoding.ASCII.GetBytes("tampered"))));
        var cache = PackageCache.Open(_folder, [], verifyFiles: false);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new CommunityManifests(http).CacheAsync(cache, "Vendor.Tool", "1.2", _ => { }, TestContext.Current.CancellationToken));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(cache.FilesFolder));
        Assert.Empty(cache.Versions);
    }

    [Fact]
    public async Task CacheAsync_RejectsAnInstallerWithoutHttps()
    {
        var responses = Responses(new string('0', 64), []);
        responses["https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/v/Vendor/Tool/1.2/Vendor.Tool.installer.yaml"] =
            "PackageIdentifier: Vendor.Tool\nInstallers:\n- Architecture: x64\n  InstallerUrl: http://example.org/tool.msi\n  InstallerSha256: " + new string('0', 64) + "\n";
        using var http = new HttpClient(new FakeHandler(responses));

        await Assert.ThrowsAsync<InvalidDataException>(() => new CommunityManifests(http).CacheAsync(PackageCache.Open(_folder, [], false), "Vendor.Tool", "1.2", _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CacheAsync_ReportsAMissingManifest()
    {
        using var http = new HttpClient(new FakeHandler([]));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new CommunityManifests(http).CacheAsync(PackageCache.Open(_folder, [], false), "Vendor.Tool", "1.2", _ => { }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public void Open_LeavesOutEntriesWhoseFileIsMissingOrChanged()
    {
        var cache = PackageCache.Open(_folder, [], verifyFiles: false);
        Directory.CreateDirectory(cache.FilesFolder);
        var good = Version("Vendor.Good", "1.0", "good");
        var changed = Version("Vendor.Changed", "1.0", "changed");
        cache.Put(good.Version);
        cache.Put(changed.Version);
        cache.Put(Version("Vendor.Missing", "1.0", "missing").Version);
        File.WriteAllText(good.Path, "good");
        File.WriteAllText(changed.Path, "tampered");
        File.WriteAllText(Path.Combine(_folder, "files", "unused.txt"), string.Empty);
        var problems = new List<string>();

        var opened = PackageCache.Open(_folder, problems, verifyFiles: true);

        Assert.Equal(["Vendor.Good"], opened.Versions.Select(version => version.PackageIdentifier));
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Open_LeavesOutEntriesThatPointOutOfTheCache()
    {
        File.WriteAllText(Path.Combine(_folder, PackageCache.IndexFileName), """
            [{"PackageIdentifier":"a.b","PackageVersion":"1","DefaultLocale":{},"Installers":[{"InstallerSha256":"00","CachedFile":"..\\..\\secret.txt"}]},
             {"PackageVersion":"1"}]
            """);
        var problems = new List<string>();

        Assert.Empty(PackageCache.Open(_folder, problems, verifyFiles: false).Versions);
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Put_ReplacesTheSameVersion()
    {
        var cache = PackageCache.Open(_folder, [], verifyFiles: false);
        cache.Put(Version("Vendor.Tool", "1.0", "a").Version);
        cache.Put(Version("vendor.tool", "1.0", "b").Version);
        cache.Put(Version("Vendor.Tool", "2.0", "c").Version);

        Assert.Equal(["1.0", "2.0"], cache.Versions.Select(version => version.PackageVersion));
    }

    [Fact]
    public void Information_NamesTheSourceAndSchema()
    {
        var answer = Responder().Information();

        Assert.Equal(200, answer.Status);
        Assert.Equal("1.1.0", answer.Body!["Data"]!["ServerSupportedVersions"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Search_FindsByKeywordAndListsVersionsNewestFirst()
    {
        var answer = Responder().Search(Request("""{"Query":{"KeyWord":"tool","MatchType":"Substring"}}"""));

        var package = Assert.Single(answer.Body!["Data"]!.AsArray())!;
        Assert.Equal("Vendor.Tool", package["PackageIdentifier"]!.GetValue<string>());
        Assert.Equal(["10.0", "9.0"], package["Versions"]!.AsArray().Select(version => version!["PackageVersion"]!.GetValue<string>()));
        Assert.Equal("{PC-10}", package["Versions"]![0]!["ProductCodes"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Search_AppliesFiltersAndInclusions()
    {
        var responder = Responder();

        Assert.Equal(204, responder.Search(Request("""{"Filters":[{"PackageMatchField":"PackageIdentifier","RequestMatch":{"KeyWord":"vendor.tool","MatchType":"Exact"}}]}""")).Status);
        Assert.Equal(200, responder.Search(Request("""{"Filters":[{"PackageMatchField":"PackageIdentifier","RequestMatch":{"KeyWord":"vendor.tool","MatchType":"CaseInsensitive"}}]}""")).Status);
        Assert.Equal(200, responder.Search(Request("""{"Inclusions":[{"PackageMatchField":"ProductCode","RequestMatch":{"KeyWord":"{pc-9}","MatchType":"CaseInsensitive"}},{"PackageMatchField":"PackageName","RequestMatch":{"KeyWord":"none","MatchType":"Exact"}}]}""")).Status);
        Assert.Equal(200, responder.Search(Request("""{"Inclusions":[{"PackageMatchField":"NormalizedPackageNameAndPublisher","RequestMatch":{"KeyWord":"toolvendor","MatchType":"Exact"}}]}""")).Status);
        Assert.Equal(204, responder.Search(Request("""{"Query":{"KeyWord":"tool"},"Filters":[{"PackageMatchField":"Command","RequestMatch":{"KeyWord":"other","MatchType":"Exact"}}]}""")).Status);
    }

    [Fact]
    public void Search_ReportsUnsupportedFieldsAndHonoursTheLimit()
    {
        var cache = PackageCache.Open(_folder, [], verifyFiles: false);
        cache.Put(Version("Vendor.One", "1.0", "1").Version);
        cache.Put(Version("Vendor.Two", "1.0", "2").Version);
        var responder = new RestSourceResponder(cache, new Uri("https://central:7443/files/"));

        var answer = responder.Search(Request("""{"MaximumResults":1,"Query":{"KeyWord":"vendor"},"Inclusions":[{"PackageMatchField":"Market","RequestMatch":{"KeyWord":"DE"}},{"PackageMatchField":"PackageIdentifier","RequestMatch":{"KeyWord":"vendor"}}]}"""));

        Assert.Single(answer.Body!["Data"]!.AsArray());
        Assert.Equal("Market", answer.Body["UnsupportedPackageMatchFields"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Search_WithoutKeywordFindsNothing()
    {
        Assert.Equal(204, Responder().Search(Request("""{"Query":{"MatchType":"Exact"}}""")).Status);
        Assert.Equal(200, Responder().Search(null).Status);
    }

    [Fact]
    public void Manifest_ServesTheInstallersFromTheCache()
    {
        var answer = Responder().Manifest("vendor.tool", "9.0");

        var version = Assert.Single(answer.Body!["Data"]!["Versions"]!.AsArray())!;
        var installer = version["Installers"]![0]!;
        Assert.Equal("https://central:7443/files/" + new string('9', 64) + ".msi", installer["InstallerUrl"]!.GetValue<string>());
        Assert.Null(installer["CachedFile"]);
        Assert.Equal("Tool", version["DefaultLocale"]!["PackageName"]!.GetValue<string>());
    }

    [Fact]
    public void Manifest_IsNotFoundForUnknownPackagesAndVersions()
    {
        Assert.Equal(404, Responder().Manifest("Vendor.Other").Status);
        Assert.Equal(404, Responder().Manifest("Vendor.Tool", "1.0").Status);
        Assert.Equal(2, Responder().Manifest("Vendor.Tool").Body!["Data"]!["Versions"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("14.51.36247.0", "14.51.36247", 0)]
    [InlineData("26.06", "24.08", 1)]
    [InlineData("1.0-beta", "1.0-alpha", 1)]
    public void VersionComparer_ComparesNumbersAsNumbers(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionComparer.Instance.Compare(left, right)));
    }

    private static JsonObject Request(string json) => (JsonObject)JsonNode.Parse(json)!;

    private RestSourceResponder Responder()
    {
        var cache = PackageCache.Open(_folder, [], verifyFiles: false);
        cache.Put(Version("Vendor.Tool", "9.0", "9").Version);
        cache.Put(Version("Vendor.Tool", "10.0", "10").Version);
        return new RestSourceResponder(cache, new Uri("https://central:7443/files/"));
    }

    /// <summary>A cached version with one installer; the hash is made of the digits of <paramref name="tag"/>.</summary>
    private (CachedVersion Version, string Path) Version(string id, string version, string tag)
    {
        var hash = tag.All(char.IsAsciiDigit) ? new string(tag[0], 64) : Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(tag)));
        var file = hash + ".msi";
        var installer = new JsonObject
        {
            ["Architecture"] = "x64",
            ["InstallerSha256"] = hash.ToUpperInvariant(),
            ["InstallerUrl"] = "https://example.org/tool.msi",
            ["ProductCode"] = "{PC-" + tag + "}",
            ["Commands"] = new JsonArray("tool"),
            ["CachedFile"] = file,
        };
        var locale = new JsonObject { ["PackageLocale"] = "en-US", ["PackageName"] = "Tool", ["Publisher"] = "Vendor" };
        return (new CachedVersion(id, version, locale, [installer]), Path.Combine(_folder, PackageCache.FilesFolderName, file));
    }

    private static Dictionary<string, object> Responses(string hash, byte[] installer)
    {
        const string Folder = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/v/Vendor/Tool/1.2/";
        return new Dictionary<string, object>
        {
            [Folder + "Vendor.Tool.yaml"] = "PackageIdentifier: Vendor.Tool\nPackageVersion: '1.2'\nDefaultLocale: en-US\nManifestType: version\n",
            [Folder + "Vendor.Tool.installer.yaml"] = $"PackageIdentifier: Vendor.Tool\nInstallers:\n- Architecture: x64\n  InstallerType: msi\n  InstallerUrl: https://example.org/tool.msi\n  InstallerSha256: {hash}\n",
            [Folder + "Vendor.Tool.locale.en-US.yaml"] = "PackageIdentifier: Vendor.Tool\nPackageLocale: en-US\nPackageName: Tool\nPublisher: Vendor\nManifestType: defaultLocale\n",
            ["https://example.org/tool.msi"] = installer,
        };
    }

    /// <summary>Answers from a table; <paramref name="stall"/> picks addresses whose body never arrives.</summary>
    private sealed class FakeHandler(Dictionary<string, object> responses, Func<string, bool>? stall = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!responses.TryGetValue(url, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            HttpContent content = stall?.Invoke(url) == true
                ? new StreamContent(new StallingStream())
                : body is byte[] bytes ? new ByteArrayContent(bytes) : new StringContent((string)body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>A body that never sends a byte, like a hanging mirror.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
