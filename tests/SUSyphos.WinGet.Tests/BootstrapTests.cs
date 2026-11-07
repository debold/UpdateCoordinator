using System.Text;

namespace SUSyphos.WinGet.Tests;

public sealed class BootstrapTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("susyphos-winget-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Touch(string relative)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void FindOfflinePackages_TakesTheFilesOfAGitHubRelease()
    {
        var bundle = Touch("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle");
        var license = Touch("e53e159d00e04f729cc2180cffd1c02e_License1.xml");
        var vclibs = Touch(@"DesktopAppInstaller_Dependencies\x64\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx");
        var xaml = Touch(@"DesktopAppInstaller_Dependencies\x64\Microsoft.UI.Xaml.2.8_8.2310.30001.0_x64.appx");
        var runtime = Touch("Microsoft.WindowsAppRuntime.1.8_8000.616.304.0_x64.msix");
        Touch(@"DesktopAppInstaller_Dependencies\arm64\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_arm64.appx");
        Touch(@"DesktopAppInstaller_Dependencies\x86\Microsoft.UI.Xaml.2.8_8.2310.30001.0_x86.appx");
        Touch("readme.txt");

        var set = WinGetBootstrap.FindOfflinePackages(_folder);

        Assert.Equal(bundle, set.Bundle);
        Assert.Equal(license, set.License);
        Assert.Equal(new[] { xaml, vclibs, runtime }.Order(StringComparer.OrdinalIgnoreCase), set.Dependencies);
    }

    [Fact]
    public void FindOfflinePackages_GoesWithoutLicenseWhenItIsNotClear()
    {
        Touch("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle");
        Touch("a_License1.xml");
        Touch("b_License1.xml");

        Assert.Null(WinGetBootstrap.FindOfflinePackages(_folder).License);
    }

    [Fact]
    public void FindOfflinePackages_NeedsExactlyOneBundle()
    {
        Touch("Other.msixbundle");
        Assert.Contains("No App Installer bundle", Assert.Throws<InvalidOperationException>(() => WinGetBootstrap.FindOfflinePackages(_folder)).Message, StringComparison.Ordinal);

        Touch("Microsoft.DesktopAppInstaller_1.msixbundle");
        Touch(@"old\Microsoft.DesktopAppInstaller_2.msixbundle");
        Assert.Contains("More than one", Assert.Throws<InvalidOperationException>(() => WinGetBootstrap.FindOfflinePackages(_folder)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindOfflinePackages_RejectsAMissingFolder()
    {
        Assert.Throws<DirectoryNotFoundException>(() => WinGetBootstrap.FindOfflinePackages(Path.Combine(_folder, "missing")));
        Assert.ThrowsAny<ArgumentException>(() => WinGetBootstrap.FindOfflinePackages(" "));
    }

    [Theory]
    [InlineData(@"C:\Temp\winget", @"'C:\Temp\winget'")]
    [InlineData("C:\\O'Brien\\x", "'C:\\O''Brien\\x'")]
    [InlineData("$(Remove-Item C:\\)", "'$(Remove-Item C:\\)'")]
    [InlineData("a\u2019b", "'a\u2019\u2019b'")]
    [InlineData("a\u201Bb", "'a\u201B\u201Bb'")]
    [InlineData("", "''")]
    public void Quote_MakesALiteralPowerShellCannotEscape(string value, string expected)
    {
        Assert.Equal(expected, WinGetBootstrap.Quote(value));
    }

    [Fact]
    public void OfflineScript_ProvisionsWithDependenciesAndLicense()
    {
        var script = WinGetBootstrap.OfflineScript(new OfflinePackageSet(@"C:\w\App.msixbundle", [@"C:\w\a.appx", @"C:\w\b'c.appx"], @"C:\w\L.xml"));

        Assert.Contains(@"Add-AppxProvisionedPackage -Online -PackagePath 'C:\w\App.msixbundle' -DependencyPackagePath @('C:\w\a.appx', 'C:\w\b''c.appx') -LicensePath 'C:\w\L.xml'", script, StringComparison.Ordinal);
        Assert.StartsWith("$ErrorActionPreference = 'Stop'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineScript_SkipsTheLicenseWhenThereIsNone()
    {
        var script = WinGetBootstrap.OfflineScript(new OfflinePackageSet(@"C:\w\App.msixbundle", [], null));

        Assert.Contains("-SkipLicense", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-DependencyPackagePath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlineScript_RepairsWinGetForAllUsers()
    {
        var script = WinGetBootstrap.OnlineScript();

        Assert.Contains("Install-Module -Name Microsoft.WinGet.Client -Repository PSGallery -Scope AllUsers", script, StringComparison.Ordinal);
        Assert.Contains("Repair-WinGetPackageManager -AllUsers", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_IsBase64OfUtf16()
    {
        Assert.Equal("Get-Date", Encoding.Unicode.GetString(Convert.FromBase64String(WinGetBootstrap.Encode("Get-Date"))));
    }

    [Theory]
    [InlineData("Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe", "1.29.380.0")]
    [InlineData("microsoft.desktopappinstaller_1.21.3482.0_X64__8wekyb3d8bbwe", "1.21.3482.0")]
    public void VersionFromFolder_ReadsX64Folders(string folder, string version)
    {
        Assert.Equal(Version.Parse(version), AppInstallerLocator.VersionFromFolder(folder));
    }

    [Theory]
    [InlineData("Microsoft.DesktopAppInstaller_1.29.380.0_arm64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.DesktopAppInstaller_1.29.380.0_neutral_~_8wekyb3d8bbwe")]
    [InlineData("Microsoft.DesktopAppInstaller_x_x64__8wekyb3d8bbwe")]
    [InlineData("Contoso.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.DesktopAppInstaller_1.0.0.0_x64__otherpublisher")]
    [InlineData("")]
    public void VersionFromFolder_IgnoresOtherFolders(string folder)
    {
        Assert.Null(AppInstallerLocator.VersionFromFolder(folder));
    }

    [Fact]
    public void ScanWindowsApps_FindsNewestFirstAndKnowsTheEngine()
    {
        Touch(@"Microsoft.DesktopAppInstaller_1.21.0.0_x64__8wekyb3d8bbwe\winget.exe");
        Touch(@"Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\" + WinGetEngine.EngineFileName);
        Touch(@"Microsoft.DesktopAppInstaller_1.30.0.0_arm64__8wekyb3d8bbwe\" + WinGetEngine.EngineFileName);
        var problems = new List<string>();

        var found = AppInstallerLocator.ScanWindowsApps(_folder, problems);

        Assert.Equal([new Version(1, 29, 380, 0), new Version(1, 21, 0, 0)], found.Select(package => package.Version));
        Assert.Equal([true, false], found.Select(package => package.HasEngine));
        Assert.Empty(problems);
    }

    [Fact]
    public void ScanWindowsApps_ReportsAFolderItCannotRead()
    {
        var problems = new List<string>();

        Assert.Empty(AppInstallerLocator.ScanWindowsApps(Path.Combine(_folder, "missing"), problems));
        Assert.Single(problems);
    }

    [Fact]
    public void RuntimeFolders_TakesTheNewestX64VCLibs()
    {
        var appInstaller = Path.Combine(_folder, "Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe");
        Touch(@"Microsoft.VCLibs.140.00.UWPDesktop_14.0.30704.0_x64__8wekyb3d8bbwe\msvcp140.dll");
        Touch(@"Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe\msvcp140.dll");
        Touch(@"Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x86__8wekyb3d8bbwe\msvcp140.dll");

        Assert.Equal(
            [Path.Combine(_folder, "Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe")],
            AppInstallerLocator.RuntimeFolders(appInstaller));
    }

    [Fact]
    public void Engine_RefusesAFolderWithoutEngine()
    {
        Assert.False(WinGetEngine.IsEngineFolder(_folder));
        Assert.Throws<FileNotFoundException>(() => WinGetEngine.Load(_folder));
    }
}
