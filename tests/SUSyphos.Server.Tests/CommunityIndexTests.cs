using SUSyphos.TestSupport;
using SUSyphos.WinGet.Catalog;

namespace SUSyphos.Server.Tests;

/// <summary>Reading the package index of the WinGet community source (schema version 2) without the WinGet engine.</summary>
public sealed class CommunityIndexTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SUSyphos.Tests", "index-" + Guid.NewGuid().ToString("N"));

    private static readonly TestIndexPackage[] Packages =
    [
        new("7zip.7zip", "7-Zip", "26.06", "7zip", ["compression", "zip"], ["{23170F69-40C1-2702-2600-000001000000}"]),
        new("mcmilk.7zip-zstd", "7-Zip ZS", "25.01", Tags: ["zip"]),
        new("Notepad++.Notepad++", "Notepad++", "8.9.9", "notepad++", ["editor"]),
        new("Vendor.Shared1", "Shared One", "1.0", ProductCodes: ["{11111111-1111-1111-1111-111111111111}"]),
        new("Vendor.Shared2", "Shared Two", "1.0", ProductCodes: ["{11111111-1111-1111-1111-111111111111}"]),
        new("Odd.Percent", "100% Tool", "1.0"),
    ];

    private CommunityIndex Index() => CommunityIndex.Open(TestCommunityIndex.Create(Path.Combine(_directory, "index.db"), Packages));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Find_IgnoresCase_AndKnowsTheNewestVersion()
    {
        var index = Index();

        Assert.Equal(new IndexPackage("7zip.7zip", "7-Zip", "7zip", "26.06"), index.Find("7ZIP.7ZIP"));
        Assert.Null(index.Find("7zip"));
        Assert.Equal(Packages.Length, index.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794163375), index.LastWrite);
    }

    [Fact]
    public void Search_FindsByIdNameMonikerAndTag_BestMatchFirst()
    {
        var index = Index();

        Assert.Equal(["7zip.7zip", "mcmilk.7zip-zstd"], index.Search("7zip").Select(p => p.Id));
        Assert.Equal("Notepad++.Notepad++", Assert.Single(index.Search("editor")).Id);
        Assert.Equal(["7zip.7zip", "mcmilk.7zip-zstd"], index.Search("zip").Select(p => p.Id).Order());
        Assert.Single(index.Search("7zip", limit: 1));
        Assert.Empty(index.Search("   "));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("' OR 1=1 --")]
    public void Search_TakesWildcardsAndQuotesLiterally(string text)
    {
        var results = Index().Search(text);

        Assert.All(results, p => Assert.Contains(text, p.Name + p.Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Search_ForAPercentSign_FindsOnlyNamesWithIt()
    {
        Assert.Equal("Odd.Percent", Assert.Single(Index().Search("%")).Id);
    }

    [Fact]
    public void ByProductCode_IgnoresCase_AndReturnsEveryOwner()
    {
        var index = Index();

        Assert.Equal("7zip.7zip", Assert.Single(index.ByProductCode("{23170f69-40c1-2702-2600-000001000000}")).Id);
        Assert.Equal(2, index.ByProductCode("{11111111-1111-1111-1111-111111111111}").Count);
        Assert.Empty(index.ByProductCode("{00000000-0000-0000-0000-000000000000}"));
    }

    [Fact]
    public void Open_RefusesOtherFiles_AndOtherSchemaVersions()
    {
        var text = Path.Combine(_directory, "text.db");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(text, "not a database");
        var old = TestCommunityIndex.Create(Path.Combine(_directory, "v1.db"), Packages, majorVersion: "1");

        Assert.Throws<InvalidDataException>(() => CommunityIndex.Open(text));
        Assert.Contains("version 1", Assert.Throws<InvalidDataException>(() => CommunityIndex.Open(old)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ReplacesTheIndex_OnlyWithAValidOne()
    {
        var database = Path.Combine(_directory, "index.db");
        var good = TestCommunityIndex.CreatePackage(Path.Combine(_directory, "good.msix"), TestCommunityIndex.Create(Path.Combine(_directory, "source.db"), Packages));

        Assert.Equal(Packages.Length, CommunityIndex.Extract(good, database).Count);

        var bad = Path.Combine(_directory, "bad.msix");
        using (var zip = System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("AppxManifest.xml");
        }

        var wrong = TestCommunityIndex.CreatePackage(Path.Combine(_directory, "wrong.msix"), TestCommunityIndex.Create(Path.Combine(_directory, "v1.db"), Packages, majorVersion: "1"));

        Assert.Throws<InvalidDataException>(() => CommunityIndex.Extract(bad, database));
        Assert.Throws<InvalidDataException>(() => CommunityIndex.Extract(wrong, database));
        Assert.Equal(Packages.Length, CommunityIndex.Open(database).Count);
        Assert.False(File.Exists(database + ".new"));
    }
}
