using Microsoft.Extensions.Logging.Abstractions;

using SUSyphos.Agent.Installation;

namespace SUSyphos.Agent.Tests;

public sealed class UpdateUninstallerTests
{
    private sealed class FakeWua(UninstallResult? result) : IWuaUninstall
    {
        public UninstallResult? TryUninstall(Guid updateId) => result;
    }

    private sealed class FakeDism : IDismUninstall
    {
        public List<string> Calls { get; } = [];

        public UninstallResult Uninstall(string kbArticle)
        {
            Calls.Add(kbArticle);
            return new UninstallResult(UninstallStatus.Succeeded, true, "Uninstalled with DISM.");
        }
    }

    [Fact]
    public async Task WindowsUpdate_IsTriedFirst()
    {
        var dism = new FakeDism();
        var uninstaller = new UpdateUninstaller(new FakeWua(new UninstallResult(UninstallStatus.Succeeded, false, "WUA")), dism, NullLogger<UpdateUninstaller>.Instance);

        var result = await uninstaller.UninstallAsync(Guid.NewGuid(), "5040434", TestContext.Current.CancellationToken);

        Assert.Equal("WUA", result.Detail);
        Assert.Empty(dism.Calls);
    }

    [Fact]
    public async Task WhatWindowsUpdateCannotRemove_GoesToDismByKb()
    {
        var dism = new FakeDism();
        var uninstaller = new UpdateUninstaller(new FakeWua(null), dism, NullLogger<UpdateUninstaller>.Instance);

        var result = await uninstaller.UninstallAsync(Guid.NewGuid(), "5040434", TestContext.Current.CancellationToken);

        Assert.Equal(["5040434"], dism.Calls);
        Assert.True(result.RebootRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("12")]
    [InlineData("5040434 /Quiet")]
    [InlineData("KB5040434")]
    public async Task WithoutAValidKb_DismIsNotCalled(string? kb)
    {
        var dism = new FakeDism();
        var uninstaller = new UpdateUninstaller(new FakeWua(null), dism, NullLogger<UpdateUninstaller>.Instance);

        var result = await uninstaller.UninstallAsync(Guid.NewGuid(), kb, TestContext.Current.CancellationToken);

        Assert.Equal(UninstallStatus.NotUninstallable, result.Status);
        Assert.Empty(dism.Calls);
    }
}

public sealed class DismUninstallTests
{
    private const string Table = """
        Deployment Image Servicing and Management tool
        Version: 10.0.20348.1

        Packages listing:

        ------------------------------------------------------------------------------------------------ | --------- | --------------- | -------------------
        Package Identity                                                                                 | State     | Release Type    | Install Time
        ------------------------------------------------------------------------------------------------ | --------- | --------------- | -------------------
        Package_for_KB5012170~31bf3856ad364e35~amd64~~20348.880.1.1                                      | Installed | Security Update | 8/10/2022 2:13 AM
        Package_for_RollupFix~31bf3856ad364e35~amd64~~20348.2762.1.7                                     | Installed | Security Update | 10/9/2026 3:00 AM
        Package_for_RollupFix~31bf3856ad364e35~amd64~~20348.2700.1.4                                     | Superseded | Security Update | 9/10/2026 3:00 AM
        Package_for_ServicingStack_2760~31bf3856ad364e35~amd64~~20348.2760.1.0                           | Installed | Update          | 10/9/2026 3:00 AM
        The operation completed successfully.
        """;

    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Commands { get; } = [];

        public string Info { get; set; } = "Support Information : https://support.microsoft.com/help/5066835";

        public int RemoveExitCode { get; set; }

        public (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            var command = string.Join(' ', arguments);
            Commands.Add(command);
            return command.Contains("/Get-Packages", StringComparison.Ordinal) ? (0, Table)
                : command.Contains("/Get-PackageInfo", StringComparison.Ordinal) ? (0, command.Contains("RollupFix", StringComparison.Ordinal) ? Info : "Support Information : https://support.microsoft.com/help/5012170")
                : (RemoveExitCode, string.Empty);
        }
    }

    [Fact]
    public void Table_YieldsInstalledPackagesOnly()
    {
        Assert.Equal(
            ["Package_for_KB5012170~31bf3856ad364e35~amd64~~20348.880.1.1", "Package_for_RollupFix~31bf3856ad364e35~amd64~~20348.2762.1.7", "Package_for_ServicingStack_2760~31bf3856ad364e35~amd64~~20348.2760.1.0"],
            DismUninstall.InstalledPackages(Table));
    }

    [Fact]
    public void PackageNamedByKb_IsRemovedDirectly()
    {
        var runner = new FakeRunner();

        var result = new DismUninstall(runner).Uninstall("5012170");

        Assert.Equal(UninstallStatus.Succeeded, result.Status);
        Assert.DoesNotContain(runner.Commands, c => c.Contains("/Get-PackageInfo", StringComparison.Ordinal));
        Assert.Contains("/Remove-Package /PackageName:Package_for_KB5012170~31bf3856ad364e35~amd64~~20348.880.1.1 /Quiet /NoRestart", runner.Commands[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void CumulativeUpdate_IsFoundByItsSupportLink_AndAsksForARestart()
    {
        var runner = new FakeRunner { RemoveExitCode = DismUninstall.RestartRequired };

        var result = new DismUninstall(runner).Uninstall("5066835");

        Assert.Equal(UninstallStatus.Succeeded, result.Status);
        Assert.True(result.RebootRequired);
        Assert.Contains("Package_for_RollupFix~31bf3856ad364e35~amd64~~20348.2762.1.7", runner.Commands[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKb_IsNotInstalled_AndFailingRemovalIsReported()
    {
        var runner = new FakeRunner();
        Assert.Equal(UninstallStatus.NotInstalled, new DismUninstall(runner).Uninstall("5999999").Status);
        Assert.DoesNotContain(runner.Commands, c => c.Contains("/Remove-Package", StringComparison.Ordinal));

        var failing = new FakeRunner { RemoveExitCode = unchecked((int)0x800F0825) };
        Assert.Equal(UninstallStatus.Failed, new DismUninstall(failing).Uninstall("5012170").Status);
    }
}
