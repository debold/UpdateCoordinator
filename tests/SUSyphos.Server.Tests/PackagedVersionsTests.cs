using System.IO.Compression;
using System.Text;

using SUSyphos.Server.Software;

namespace SUSyphos.Server.Tests;

/// <summary>The versions inside cached MSIX installers and their translation into WinGet versions (lab, 2026-11-25).</summary>
public sealed class PackagedVersionsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("SUSyphos.Tests.msix-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Archive(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_folder, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }

    [Fact]
    public void Bundle_GivesTheBundleAndEveryPackageVersion()
    {
        var path = Archive("a.bin", ("AppxMetadata/AppxBundleManifest.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle" SchemaVersion="5.0">
              <Identity Name="Microsoft.PowerShell" Publisher="CN=Microsoft" Version="2026.1110.418.0" />
              <Packages>
                <Package Type="application" Version="2026.1110.418.0" Architecture="x64" FileName="x64.msix" />
                <Package Type="application" Version="2026.1110.418.1" Architecture="arm64" FileName="arm64.msix" />
                <Package Type="application" Version="not-a-version" Architecture="x86" FileName="x86.msix" />
              </Packages>
            </Bundle>
            """));

        Assert.Equal(["2026.1110.418.0", "2026.1110.418.1"], PackagedVersions.Read(path));
    }

    [Fact]
    public void SinglePackage_GivesItsIdentityVersion()
    {
        var path = Archive("b.msix", ("AppxManifest.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Vendor.App" Publisher="CN=Vendor" Version="1.2.3.0" ProcessorArchitecture="x64" />
            </Package>
            """));

        Assert.Equal(["1.2.3.0"], PackagedVersions.Read(path));
    }

    [Fact]
    public void OtherFiles_GiveNothing()
    {
        var exe = Path.Combine(_folder, "setup.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A, 0, 1, 2, 3]);
        var zip = Archive("c.zip", ("readme.txt", "nothing packaged"));
        var dtd = Archive("d.msix", ("AppxManifest.xml", """
            <?xml version="1.0"?>
            <!DOCTYPE Package [<!ENTITY x "1.0.0.0">]>
            <Package><Identity Version="&x;" /></Package>
            """));

        Assert.Empty(PackagedVersions.Read(exe));
        Assert.Empty(PackagedVersions.Read(zip));
        Assert.Empty(PackagedVersions.Read(dtd));
        Assert.Empty(PackagedVersions.Read(Path.Combine(_folder, "missing.msix")));
    }

    [Fact]
    public void ReportedVersion_IsTranslated_OnlyWhenAnInstallerHasIt()
    {
        (string, string?)[] versions = [("7.7.0.0", "2026.1110.418.0 2026.1110.418.1"), ("7.6.9.0", null)];

        Assert.Equal("7.7.0.0", PackagedVersions.ToPackageVersion(versions, "2026.1110.418.1"));
        Assert.Equal("2026.1201.1.0", PackagedVersions.ToPackageVersion(versions, "2026.1201.1.0"));
        Assert.Equal("2026.1110.418", PackagedVersions.ToPackageVersion(versions, "2026.1110.418"));
        Assert.Null(PackagedVersions.ToPackageVersion(versions, null));
    }

    [Fact]
    public void Join_And_HasPackages()
    {
        Assert.Equal("1.0.0.0 2.0.0.0", PackagedVersions.Join(["1.0.0.0", "2.0.0.0", "1.0.0.0"]));
        Assert.Null(PackagedVersions.Join([]));
        Assert.Null(PackagedVersions.Join([.. Enumerable.Range(0, 40).Select(i => $"2026.1110.{i}.0")]));
        Assert.True(PackagedVersions.HasPackages("{\"Installers\":[{\"InstallerType\":\"msix\"}]}"));
        Assert.True(PackagedVersions.HasPackages("{\"InstallerType\":\"APPX\"}"));
        Assert.False(PackagedVersions.HasPackages("{\"Installers\":[{\"InstallerType\":\"msi\"}]}"));
        Assert.False(PackagedVersions.HasPackages(null));
    }
}
