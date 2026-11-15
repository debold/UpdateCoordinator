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

    [Fact]
    public async Task UntrustedCertificate_IsReportedWithItsCode()
    {
        var outcome = new SoftwareInstallOutcome { Succeeded = false, Status = "SourceUnavailable", Detail = "This server does not trust the certificate of the central server.", HResult = unchecked((int)0x800B0109) };
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, JsonSerializer.Serialize(outcome, SoftwareInventoryReport.JsonOptions)) };

        var result = Assert.Single(await Installer(runner).InstallAsync([Software("7zip.7zip", "26.06")], TestContext.Current.CancellationToken));

        Assert.Equal((InstallResultCode.Failed, unchecked((int)0x800B0109)), (result.ResultCode, result.HResult));
        Assert.Equal("SourceUnavailable (0x800B0109): This server does not trust the certificate of the central server.", SoftwareInstaller.FailureText(outcome));
    }

    [Fact]
    public void FailureText_NamesTheInstallerExitCodeOnlyWhenAnInstallerRan()
    {
        Assert.Equal("InstallError, installer exit code 1603: failed", SoftwareInstaller.FailureText(new SoftwareInstallOutcome { Status = "InstallError", InstallerExitCode = 1603, Detail = "failed" }));
        Assert.Equal("Error", SoftwareInstaller.FailureText(new SoftwareInstallOutcome { Status = "Error" }));
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

        // A helper stopped after the time limit reports its own code, so the dashboard can say what happened.
        Assert.Equal(output is null ? SoftwareInstaller.TimedOut : SoftwareInstaller.Failed, result.HResult);
    }

    [Fact]
    public async Task InstallerError_IsAFailedInstallation()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(false)) };

        var result = Assert.Single(await Installer(runner).InstallAsync([Software("7zip.7zip", "26.06")], TestContext.Current.CancellationToken));

        Assert.Equal(InstallResultCode.Failed, result.ResultCode);
        Assert.Equal(1603, result.InstallerExitCode);
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

    [Fact]
    public async Task Uninstall_RunsTheHelper_AndMapsTheOutcome()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(true)) };

        var removed = await Installer(runner).UninstallAsync("7zip.7zip", null, TestContext.Current.CancellationToken);

        Assert.Equal(["uninstall", "7zip.7zip"], Assert.Single(runner.Calls));
        Assert.Equal(UninstallStatus.Succeeded, removed.Status);
    }

    [Fact]
    public async Task Uninstall_NotInstalledOrFailing_IsReportedSo()
    {
        var notInstalled = new SoftwareInventoryTests.FakeRunner { Result = (0, JsonSerializer.Serialize(new SoftwareInstallOutcome { Succeeded = false, Status = "NotInstalled", Detail = "7zip.7zip is not installed." }, SoftwareInventoryReport.JsonOptions)) };
        var broken = new SoftwareInventoryTests.FakeRunner { Result = (2, string.Empty) };

        Assert.Equal(UninstallStatus.NotInstalled, (await Installer(notInstalled).UninstallAsync("7zip.7zip", null, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(UninstallStatus.Failed, (await Installer(broken).UninstallAsync("7zip.7zip", null, TestContext.Current.CancellationToken)).Status);
    }

    private static readonly SUSyphos.Contracts.Scripts.ScriptReference Before = new(SUSyphos.Contracts.Scripts.ScriptPhase.BeforeInstall, 1, 1, "AA", "BB");
    private static readonly SUSyphos.Contracts.Scripts.ScriptReference After = new(SUSyphos.Contracts.Scripts.ScriptPhase.AfterInstall, 2, 1, "AA", "BB");

    private static Release WithScripts(string installed = "") => new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, Software: new SoftwareRelease("7zip.7zip", "26.06", false, [Before, After], installed.Length == 0 ? null : installed));

    [Fact]
    public async Task FailedScriptBefore_StopsTheInstallation()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(true)) };
        var scripts = new FakeScripts { Results = { [SUSyphos.Contracts.Scripts.ScriptPhase.BeforeInstall] = SUSyphos.Contracts.Scripts.ScriptRunResult.Failed } };

        var result = Assert.Single(await Installer(runner, scripts).InstallAsync([WithScripts()], TestContext.Current.CancellationToken));

        Assert.Equal(InstallResultCode.Failed, result.ResultCode);
        Assert.Equal(SUSyphos.Contracts.Scripts.ScriptLimits.BeforeScriptFailedCode, result.HResult);
        Assert.Empty(runner.Calls);
        Assert.Equal([SUSyphos.Contracts.Scripts.ScriptPhase.BeforeInstall], scripts.Ran);
    }

    [Theory]
    [InlineData((int)SUSyphos.Contracts.Scripts.ScriptRunResult.Blocked, unchecked((int)0xA0050003))]
    [InlineData((int)SUSyphos.Contracts.Scripts.ScriptRunResult.NotVerified, unchecked((int)0xA0050004))]
    public async Task BlockedOrUnverifiedScript_StopsEvenWhenTheInstallationMayContinue(int result, int code)
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(true)) };
        var scripts = new FakeScripts { OnFailure = SUSyphos.Contracts.Scripts.ScriptOnFailure.Continue, Results = { [SUSyphos.Contracts.Scripts.ScriptPhase.BeforeInstall] = (SUSyphos.Contracts.Scripts.ScriptRunResult)result } };

        Assert.Equal(code, Assert.Single(await Installer(runner, scripts).InstallAsync([WithScripts()], TestContext.Current.CancellationToken)).HResult);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task FailedScriptAfter_CountsAsFailed_AndUpgradeIsPassedOn()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(true)) };
        var scripts = new FakeScripts { Results = { [SUSyphos.Contracts.Scripts.ScriptPhase.AfterInstall] = SUSyphos.Contracts.Scripts.ScriptRunResult.Failed } };

        var result = Assert.Single(await Installer(runner, scripts).InstallAsync([WithScripts("26.05")], TestContext.Current.CancellationToken));

        Assert.Equal(SUSyphos.Contracts.Scripts.ScriptLimits.AfterScriptFailedCode, result.HResult);
        Assert.Single(runner.Calls);
        Assert.All(scripts.Requests, r => Assert.Equal((SUSyphos.Contracts.Scripts.ScriptAction.Upgrade, "26.05"), (r.Action, r.PreviousVersion)));
    }

    [Fact]
    public async Task ScriptAfter_ThatNeedsARestart_MarksTheInstallation()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(true)) };
        var scripts = new FakeScripts { Results = { [SUSyphos.Contracts.Scripts.ScriptPhase.AfterInstall] = SUSyphos.Contracts.Scripts.ScriptRunResult.SucceededRestart } };

        var result = Assert.Single(await Installer(runner, scripts).InstallAsync([WithScripts()], TestContext.Current.CancellationToken));

        Assert.Equal(InstallResultCode.Succeeded, result.ResultCode);
        Assert.True(result.RebootRequired);
    }

    [Fact]
    public async Task UninstallScripts_RunAround_AndAFailedOneBeforeKeepsTheProgram()
    {
        var runner = new SoftwareInventoryTests.FakeRunner { Result = (0, Outcome(true)) };
        var scripts = new FakeScripts { Results = { [SUSyphos.Contracts.Scripts.ScriptPhase.BeforeUninstall] = SUSyphos.Contracts.Scripts.ScriptRunResult.Failed } };
        SUSyphos.Contracts.Scripts.ScriptReference[] references = [new(SUSyphos.Contracts.Scripts.ScriptPhase.BeforeUninstall, 3, 1, "AA", "BB")];

        var result = await Installer(runner, scripts).UninstallAsync("7zip.7zip", references, TestContext.Current.CancellationToken);

        Assert.Equal(UninstallStatus.Failed, result.Status);
        Assert.Empty(runner.Calls);
    }

    private static SoftwareInstaller Installer(SoftwareInventoryTests.FakeRunner runner, FakeScripts scripts) =>
        new(runner, Options.Create(new CentralServerOptions { BaseAddress = Central }), NullLogger<SoftwareInstaller>.Instance, scripts);

    private sealed class FakeScripts : SUSyphos.Agent.Scripts.IScriptRunner
    {
        public Dictionary<SUSyphos.Contracts.Scripts.ScriptPhase, SUSyphos.Contracts.Scripts.ScriptRunResult> Results { get; } = [];

        public SUSyphos.Contracts.Scripts.ScriptOnFailure OnFailure { get; init; } = SUSyphos.Contracts.Scripts.ScriptOnFailure.Stop;

        public List<SUSyphos.Contracts.Scripts.ScriptPhase> Ran { get; } = [];

        public List<SUSyphos.Agent.Scripts.ScriptRunRequest> Requests { get; } = [];

        public Task<SUSyphos.Agent.Scripts.ScriptRunOutcome> RunAsync(SUSyphos.Agent.Scripts.ScriptRunRequest request, CancellationToken cancellationToken)
        {
            Ran.Add(request.Script.Phase);
            Requests.Add(request);
            return Task.FromResult(new SUSyphos.Agent.Scripts.ScriptRunOutcome(true, Results.GetValueOrDefault(request.Script.Phase, SUSyphos.Contracts.Scripts.ScriptRunResult.Succeeded), OnFailure, "test"));
        }
    }
}