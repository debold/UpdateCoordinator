using SUSyphos.Server.Software;

namespace SUSyphos.Server.Tests;

/// <summary>Package versions a server cannot install are recognised from the manifest (Marc, 2026-11-21).</summary>
public sealed class SoftwareApplicabilityTests
{
    private const string Server2019 = "10.0.17763.9487";
    private const string Server2022 = "10.0.20348.5870";
    private const string Server2025 = "10.0.26100.33822";

    private static string Manifest(params string[] installers) => $"{{\"Installers\":[{string.Join(',', installers)}]}}";

    private const string Portable = "{\"Architecture\":\"x64\",\"InstallerType\":\"zip\",\"NestedInstallerType\":\"portable\"}";

    [Theory]
    [InlineData(Server2019)]
    [InlineData("10.0.14393.0")]
    public void PortableBelow18362_IsNotSupported(string windows) =>
        Assert.Contains("portable", SoftwareApplicability.WhyNot(Manifest(Portable), windows), StringComparison.Ordinal);

    [Theory]
    [InlineData(Server2022)]
    [InlineData(Server2025)]
    public void PortableFrom18362_Fits(string windows) =>
        Assert.Null(SoftwareApplicability.WhyNot(Manifest(Portable), windows));

    [Fact]
    public void MinimumWindowsAboveTheServer_IsNotSupported() =>
        Assert.Contains("10.0.20348", SoftwareApplicability.WhyNot(Manifest("{\"InstallerType\":\"msi\",\"MinimumOSVersion\":\"10.0.20348.0\"}"), Server2019), StringComparison.Ordinal);

    [Theory]
    [InlineData("{\"InstallerType\":\"burn\",\"MinimumOSVersion\":\"10.0.0.0\"}")]
    [InlineData("{\"InstallerType\":\"exe\"}")]
    [InlineData("{\"InstallerType\":\"exe\",\"ElevationRequirement\":\"elevatesSelf\"}")]
    public void UsualInstallers_Fit(string installer) =>
        Assert.Null(SoftwareApplicability.WhyNot(Manifest(installer), Server2019));

    [Theory]
    [InlineData("{\"InstallerType\":\"exe\",\"ElevationRequirement\":\"elevationProhibited\"}", "administrator")]
    [InlineData("{\"InstallerType\":\"msstore\"}", "Microsoft Store")]
    [InlineData("{\"InstallerType\":\"exe\",\"Scope\":\"user\"}", "one user")]
    public void InstallersTheAgentCannotRun_AreNotSupported(string installer, string reason) =>
        Assert.Contains(reason, SoftwareApplicability.WhyNot(Manifest(installer), Server2022), StringComparison.Ordinal);

    [Theory]
    [InlineData(Server2022)]
    [InlineData(Server2025)]
    public void MsixFrom19041_Fits_ForTheMachine(string windows)
    {
        Assert.Null(SoftwareApplicability.WhyNot(Manifest("{\"InstallerType\":\"msix\"}"), windows));
        Assert.Null(SoftwareApplicability.WhyNot(Manifest("{\"InstallerType\":\"msix\",\"Scope\":\"machine\"}"), windows));
        Assert.Contains("one user", SoftwareApplicability.WhyNot(Manifest("{\"InstallerType\":\"msix\",\"Scope\":\"user\"}"), windows), StringComparison.Ordinal);
    }

    [Fact]
    public void MsixBelow19041_IsNotSupported() =>
        Assert.Contains("10.0.19041", SoftwareApplicability.WhyNot(Manifest("{\"InstallerType\":\"msix\"}"), Server2019), StringComparison.Ordinal);

    [Fact]
    public void ScopeOfTheManifest_CountsForItsInstallers() =>
        Assert.Contains("one user", SoftwareApplicability.WhyNot("{\"Scope\":\"user\",\"Installers\":[{\"InstallerType\":\"exe\"}]}", Server2022), StringComparison.Ordinal);

    [Fact]
    public void OneFittingInstaller_IsEnough() =>
        Assert.Null(SoftwareApplicability.WhyNot(Manifest("{\"InstallerType\":\"msix\"}", "{\"InstallerType\":\"exe\"}"), Server2019));

    [Theory]
    [InlineData(null, Server2019)]
    [InlineData("not json", Server2019)]
    [InlineData("{\"InstallerType\":\"msix\"}", null)]
    [InlineData("{\"InstallerType\":\"msix\"}", "unknown")]
    public void UnreadableManifestOrWindows_LeavesItToWinGet(string? manifest, string? windows) =>
        Assert.Null(SoftwareApplicability.WhyNot(manifest is null || !manifest.StartsWith('{') ? manifest : Manifest(manifest), windows));
}
