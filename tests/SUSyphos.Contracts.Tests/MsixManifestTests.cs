using System.Text;

using SUSyphos.Contracts.Software;

namespace SUSyphos.Contracts.Tests;

/// <summary>The bundle and package manifests of MSIX packages (lab, 2026-11-25: PowerShell 7.7 bundle 2026.1110.418.0).</summary>
public sealed class MsixManifestTests
{
    internal const string PowerShellBundle = """
        <?xml version="1.0" encoding="UTF-8" standalone="no"?>
        <Bundle SchemaVersion="5.0" IgnorableNamespaces="b4" xmlns="http://schemas.microsoft.com/appx/2013/bundle" xmlns:b4="http://schemas.microsoft.com/appx/2018/bundle">
          <Identity Name="Microsoft.PowerShell" Publisher="CN=Microsoft Corporation" Version="2026.1110.418.0"/>
          <Packages>
            <Package Type="application" Version="7.7.0.0" Architecture="x64" FileName="PowerShell-7.7.0-win-x64.msix">
              <Resources><Resource Language="EN-US"/></Resources>
            </Package>
            <Package Type="application" Version="7.7.0.1" Architecture="arm64" FileName="PowerShell-7.7.0-win-arm64.msix"/>
            <Package Type="resource" Version="7.7.0.9" ResourceId="split.scale-200" FileName="resources.msix"/>
          </Packages>
        </Bundle>
        """;

    private static MsixIdentity? Parse(string xml) => MsixManifest.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void Bundle_GivesItsVersion_AndTheApplicationPackagePerArchitecture()
    {
        var identity = Assert.IsType<MsixIdentity>(Parse(PowerShellBundle));

        Assert.Equal("2026.1110.418.0", identity.Version);
        Assert.Equal(["7.7.0.0", "7.7.0.1", "7.7.0.9"], identity.Packages.Select(p => p.Version));
        Assert.Equal("7.7.0.0", identity.ApplicationVersion("x64"));
        Assert.Equal("7.7.0.1", identity.ApplicationVersion("ARM64"));
        Assert.Null(identity.ApplicationVersion("x86"));
    }

    [Fact]
    public void NeutralApplicationPackage_FitsEveryArchitecture()
    {
        var identity = Parse("""
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle"><Identity Version="1.0.0.0"/>
            <Packages><Package Type="application" Version="2.0.0.0" Architecture="neutral"/></Packages></Bundle>
            """);

        Assert.Equal("2.0.0.0", identity?.ApplicationVersion("x64"));
    }

    [Fact]
    public void SinglePackage_GivesItsVersion_WithoutPackages()
    {
        var identity = Parse("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="A" Version="1.2.3.0" ProcessorArchitecture="x64"/></Package>""");

        Assert.Equal("1.2.3.0", identity?.Version);
        Assert.Empty(identity!.Packages);
        Assert.Null(identity.ApplicationVersion("x64"));
    }

    [Theory]
    [InlineData("<Other><Identity Version=\"1.0.0.0\"/></Other>")]
    [InlineData("<Bundle><Identity Version=\"not a version\"/></Bundle>")]
    [InlineData("<Bundle><Identity/></Bundle>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE Bundle [<!ENTITY v \"1.0.0.0\">]><Bundle><Identity Version=\"&v;\"/></Bundle>")]
    [InlineData("not xml at all")]
    [InlineData("")]
    public void InvalidManifest_GivesNothing(string xml) => Assert.Null(Parse(xml));

    [Fact]
    public void InvalidPackageVersions_AreLeftOut()
    {
        var identity = Parse("""<Bundle><Identity Version="1.0.0.0"/><Packages><Package Type="application" Version="x" Architecture="x64"/></Packages></Bundle>""");

        Assert.Empty(identity!.Packages);
    }
}
