namespace SUSyphos.WinGet.Tests;

/// <summary>
/// A provisioned bundle is reported with the version of its application package (lab, 2026-11-25: PowerShell 7.7 is
/// provisioned as the bundle 2026.1110.418.0, its x64 package is 7.7.0.0, which WinGet and Get-AppxPackage show).
/// </summary>
public sealed class ProvisionedBundleTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("SUSyphos.Tests.bundle-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void WriteManifest(string xml)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "AppxMetadata"));
        File.WriteAllText(Path.Combine(_folder, "AppxMetadata", "AppxBundleManifest.xml"), xml);
    }

    [Fact]
    public void Bundle_IsReportedWithItsApplicationPackageVersion()
    {
        WriteManifest("""
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle"><Identity Version="2026.1110.418.0"/>
            <Packages><Package Type="application" Version="7.7.0.0" Architecture="x64"/><Package Type="application" Version="7.7.0.1" Architecture="arm64"/></Packages></Bundle>
            """);

        Assert.Equal("7.7.0.0", ProvisionedPackages.ApplicationVersion("2026.1110.418.0", _folder, "x64"));
        Assert.Equal("7.7.0.1", ProvisionedPackages.ApplicationVersion("2026.1110.418.0", _folder, "arm64"));
    }

    [Fact]
    public void WithoutAFittingPackageOrManifest_TheBundleVersionStays()
    {
        Assert.Equal("2026.1110.418.0", ProvisionedPackages.ApplicationVersion("2026.1110.418.0", null, "x64"));
        Assert.Equal("2026.1110.418.0", ProvisionedPackages.ApplicationVersion("2026.1110.418.0", _folder, "x64"));
        Assert.Equal("2026.1110.418.0", ProvisionedPackages.ApplicationVersion("2026.1110.418.0", Path.Combine(_folder, "missing"), "x64"));

        WriteManifest("""<Bundle><Identity Version="2026.1110.418.0"/><Packages><Package Type="application" Version="7.7.0.0" Architecture="x86"/></Packages></Bundle>""");
        Assert.Equal("2026.1110.418.0", ProvisionedPackages.ApplicationVersion("2026.1110.418.0", _folder, "x64"));

        WriteManifest("<Bundle><broken");
        Assert.Equal("2026.1110.418.0", ProvisionedPackages.ApplicationVersion("2026.1110.418.0", _folder, "x64"));
    }
}
