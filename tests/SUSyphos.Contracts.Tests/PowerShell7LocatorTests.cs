using SUSyphos.Contracts.Scripts;

namespace SUSyphos.Contracts.Tests;

/// <summary>pwsh.exe from the MSI folder or, for PowerShell 7.7 and later, from the provisioned MSIX package.</summary>
public sealed class PowerShell7LocatorTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string Msi = @"C:\Program Files\PowerShell\7\pwsh.exe";
    private const string Apps = @"C:\Program Files\WindowsApps";

    private static string? Find(string[] files, params string[] folders) =>
        PowerShell7Locator.Find(ProgramFiles, file => files.Contains(file, StringComparer.OrdinalIgnoreCase), path => path == Apps ? folders.Select(f => Path.Combine(Apps, f)) : []);

    private static string Pwsh(string folder) => Path.Combine(Apps, folder, "pwsh.exe");

    [Fact]
    public void MsiFolder_ComesFirst() =>
        Assert.Equal(Msi, Find([Msi, Pwsh("Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe")], "Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe"));

    [Fact]
    public void WithoutMsi_TheNewestX64Package_IsUsed()
    {
        string[] folders = ["Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe", "Microsoft.PowerShell_7.10.1.0_x64__8wekyb3d8bbwe", "Microsoft.PowerShell_7.9.0.0_x64__8wekyb3d8bbwe"];

        Assert.Equal(Pwsh(folders[1]), Find([.. folders.Select(Pwsh)], folders));
    }

    [Fact]
    public void PackageFolderWithoutPwsh_IsSkipped()
    {
        string[] folders = ["Microsoft.PowerShell_7.8.0.0_x64__8wekyb3d8bbwe", "Microsoft.PowerShell_7.7.0.0_x64__8wekyb3d8bbwe"];

        Assert.Equal(Pwsh(folders[1]), Find([Pwsh(folders[1])], folders));
    }

    [Theory]
    [InlineData("Microsoft.PowerShellPreview_7.8.0.0_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.PowerShell_7.7.0.0_x86__8wekyb3d8bbwe")]
    [InlineData("Microsoft.PowerShell_7.7.0.0_x64__otherpublisher")]
    [InlineData("Microsoft.PowerShell_7.7.0.0_neutral_~_8wekyb3d8bbwe")]
    [InlineData("Microsoft.PowerShell_..\\..\\Evil_x64__8wekyb3d8bbwe")]
    public void OtherPackages_AreNeverUsed(string folder) =>
        Assert.Null(Find([Pwsh(folder)], folder));

    [Fact]
    public void Nothing_IsNull() => Assert.Null(Find([]));
}
