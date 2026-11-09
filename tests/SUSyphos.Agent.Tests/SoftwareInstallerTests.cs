using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Agent.Installation;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

/// <summary>Installing released software versions with the WinGet helper.</summary>
public sealed class SoftwareInstallerTests
{
    private static readonly Uri Central = new("https://central.lab.local:8443/");

    private static Release Software(string id, string version, bool accept = false) => new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Software: new SoftwareRelease(id, version, accept));

    private static string Outcome(bool succeeded, bool reboot = false) =>
        JsonSerializer.Serialize(new SoftwareInstallOutcome { Succeeded = succeeded, Status = succeeded ? "Ok" : "InstallError", InstallerExitCode = succeeded ? 0u : 1603u, RebootRequired = reboot, Detail = succeeded ? null : "failed" }, SoftwareInventoryReport.JsonOptions);

    private static SoftwareInstaller Installer(SoftwareInventoryTests.FakeRunner runner, Uri? central = null) =>
        new(runner, Options.Create(new CentralServerOptions { BaseAddress = central ?? Central }), NullLogger<SoftwareInstaller>.Instance);

    [Fact]
    public async Task Version_IsInstalledFromTheCentralSource_WithAgreementsOnlyWhenAccepted()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(true, reboot: true)) };
        var plain = Software("7zip.7zip", "26.06");
        var licensed = Software("Vendor.Tool", "3.1", accept: true);

        var results = await Installer(runner).InstallAsync([plain, licensed], TestContext.Current.CancellationToken);

        Assert.Equal(["install", "7zip.7zip", "26.06", "https://central.lab.local:8443/winget/api/"], runner.Calls[0]);
        Assert.Equal(["install", "Vendor.Tool", "3.1", "https://central.lab.local:8443/winget/api/", "accept-agreements"], runner.Calls[1]);
        Assert.All(results, r => Assert.Equal(InstallResultCode.Succeeded, r.ResultCode));
        Assert.True(results[0].RebootRequired);
        Assert.Equal(SoftwareInstaller.Timeout, runner.Timeout);
    }

    [Fact]
    public async Task SeveralVersionsOfOnePackage_OnlyTheNewestIsInstalled()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(true)) };
        var older = Software("7zip.7zip", "26.9");
        var newer = Software("7zip.7zip", "26.10");

        var results = await Installer(runner).InstallAsync([older, newer], TestContext.Current.CancellationToken);

        Assert.Equal("26.10", Assert.Single(runner.Calls)[2]);
        Assert.Equal([newer.UpdateId, older.UpdateId], results.Select(r => r.UpdateId));
        Assert.All(results, r => Assert.Equal(InstallResultCode.Succeeded, r.ResultCode));
    }

    public static readonly TheoryData<int, string?> Failures = new()
    {
        { 0, null },
        { 2, "" },
        { 0, "not json" },
        { 0, "null" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task HelperFailing_IsAFailedInstallation(int exitCode, string? output)
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = output is null ? null : (exitCode, output) };

        var result = Assert.Single(await Installer(runner).InstallAsync([Software("7zip.7zip", "26.06")], TestContext.Current.CancellationToken));

        Assert.Equal(InstallResultCode.Failed, result.ResultCode);
        Assert.Equal(SoftwareInstaller.Failed, result.HResult);
    }

    [Fact]
    public async Task InstallerError_IsAFailedInstallation()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(false)) };

        var result = Assert.Single(await Installer(runner).InstallAsync([Software("7zip.7zip", "26.06")], TestContext.Current.CancellationToken));

        Assert.Equal(InstallResultCode.Failed, result.ResultCode);
    }

    [Fact]
    public async Task HelperNotStarting_IsAFailedInstallation()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Throw = new System.ComponentModel.Win32Exception() };

        var result = Assert.Single(await Installer(runner).InstallAsync([Software("7zip.7zip", "26.06")], TestContext.Current.CancellationToken));

        Assert.Equal(InstallResultCode.Failed, result.ResultCode);
    }

    [Theory]
    [InlineData("26.10", "26.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("2.0-beta", "2.0", 1)]
    [InlineData("1.2", "1.10", -1)]
    public void VersionOrder_ComparesNumbersAsNumbers(string left, string right, int sign)
    {
        Assert.Equal(sign, Math.Sign(WinGetVersionOrder.Instance.Compare(left, right)));
    }
}
