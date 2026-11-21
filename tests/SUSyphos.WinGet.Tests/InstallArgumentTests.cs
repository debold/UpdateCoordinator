namespace SUSyphos.WinGet.Tests;

/// <summary>The helper's install arguments with the architecture of the installation to update (lab, 2026-11-16).</summary>
public sealed class InstallArgumentTests
{
    [Theory]
    [InlineData("install", "Microsoft.DotNet.DesktopRuntime.10", "10.0.15", "https://central:8443/winget/api/", "arch=x64")]
    [InlineData("install", "Vendor.Tool", "3.1", "https://central:8443/winget/api/", "accept-agreements", "arch=x86")]
    [InlineData("install", "Vendor.Tool", "3.1", "https://central:8443/winget/api/", "arch=arm64", "accept-agreements")]
    public void HelperArguments_TakeTheArchitecture(params string[] arguments)
    {
        var parsed = SUSyphos.Agent.WinGet.InstallCommand.ParseWithArchitecture(arguments, out var error);

        Assert.Null(error);
        Assert.Equal(arguments.Single(a => a.StartsWith("arch=", StringComparison.Ordinal))[5..], parsed!.Value.Architecture);
        Assert.Equal(arguments.Contains("accept-agreements"), parsed.Value.AcceptAgreements);
    }

    [Theory]
    [InlineData("install", "Vendor.Tool", "3.1", "https://central:8443/winget/api/", "arch=ia64")]
    [InlineData("install", "Vendor.Tool", "3.1", "https://central:8443/winget/api/", "arch=x64", "arch=x64")]
    [InlineData("install", "Vendor.Tool", "3.1", "https://central:8443/winget/api/", "arch=x64", "anything")]
    [InlineData("install", "Vendor.Tool", "3.1", "https://central:8443/winget/api/", "accept-agreements", "arch=x64", "arch=x86")]
    public void HelperArguments_RefuseUnknownArchitectures(params string[] arguments) =>
        Assert.Null(SUSyphos.Agent.WinGet.InstallCommand.ParseWithArchitecture(arguments, out _));
}

/// <summary>Results of WinGet that the agent reports differently from WinGet's status (lab, 2026-11-20/21).</summary>
public sealed class OperationResultTests
{
    [Theory]
    [InlineData(false, 3010u)]
    [InlineData(false, 1641u)]
    [InlineData(true, 0u)]
    public void RestartExitCodes_AskForARestart(bool winGetSays, uint exitCode) =>
        Assert.True(WinGetClient.AsksForRestart(winGetSays, exitCode));

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(1603u)]
    public void OtherExitCodes_DoNotAskForARestart(uint exitCode) =>
        Assert.False(WinGetClient.AsksForRestart(false, exitCode));

    [Theory]
    [InlineData("Ok", true)]
    [InlineData(WinGetOperationResult.AlreadyInstalled, true)]
    [InlineData("NoApplicableUpgrade", false)]
    [InlineData("NoApplicableInstallers", false)]
    public void AnUpgradeWithTheVersionInstalled_Succeeds(string status, bool succeeded) =>
        Assert.Equal(succeeded, new WinGetOperationResult("upgrade", "Vendor.Tool", "1.2", status, null, 0, false, TimeSpan.Zero, "log").Succeeded);
}
