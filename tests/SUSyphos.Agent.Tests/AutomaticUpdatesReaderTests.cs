using Microsoft.Extensions.Options;

using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Updates;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class AutomaticUpdatesReaderTests
{
    private readonly FakeSystemProbe _probe = new();

    [Fact]
    public void NothingConfigured_IsNotConfiguredAndKeepsCoordination()
    {
        var info = AutomaticUpdatesReader.Read(_probe);

        Assert.Equal(AutomaticUpdateMode.NotConfigured, info.Mode);
        Assert.False(info.ByPolicy);
        Assert.False(info.BypassesCoordination());
    }

    [Theory]
    [InlineData("2", AutomaticUpdateMode.NotifyDownload, false)]
    [InlineData("3", AutomaticUpdateMode.AutoDownloadNotifyInstall, false)]
    [InlineData("4", AutomaticUpdateMode.ScheduledInstall, true)]
    [InlineData("7", AutomaticUpdateMode.NotifyInstallAndRestart, false)]
    public void PolicyMode_IsReportedAsPolicy(string value, AutomaticUpdateMode expected, bool bypasses)
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = value;

        var info = AutomaticUpdatesReader.Read(_probe);

        Assert.Equal(expected, info.Mode);
        Assert.True(info.ByPolicy);
        Assert.Equal(bypasses, info.BypassesCoordination());
    }

    [Fact]
    public void NoAutoUpdate_WinsOverOptions()
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate")] = "1";
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "4";

        Assert.Equal(AutomaticUpdateMode.Disabled, AutomaticUpdatesReader.Read(_probe).Mode);
    }

    [Fact]
    public void Policy_WinsOverLocalSetting()
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "3";
        _probe.Values[(AutomaticUpdatesReader.LocalAuPath, "AUOptions")] = "4";

        var info = AutomaticUpdatesReader.Read(_probe);

        Assert.Equal(AutomaticUpdateMode.AutoDownloadNotifyInstall, info.Mode);
        Assert.True(info.ByPolicy);
    }

    [Fact]
    public void LocalAdminChooses_UsesLocalSetting()
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "5";
        _probe.Values[(AutomaticUpdatesReader.LocalAuPath, "AUOptions")] = "4";

        var info = AutomaticUpdatesReader.Read(_probe);

        Assert.Equal(AutomaticUpdateMode.ScheduledInstall, info.Mode);
        Assert.False(info.ByPolicy);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-4")]
    public void UnknownValues_AreIgnored(string value)
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = value;
        _probe.Values[(AutomaticUpdatesReader.LocalAuPath, "AUOptions")] = value;

        Assert.Equal(AutomaticUpdateMode.NotConfigured, AutomaticUpdatesReader.Read(_probe).Mode);
    }

    [Fact]
    public void ScheduledRestartAndDeadline_BypassCoordination()
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AlwaysAutoRebootAtScheduledTime")] = "1";
        _probe.Values[(AutomaticUpdatesReader.PolicyPath, "SetComplianceDeadline")] = "1";

        var info = AutomaticUpdatesReader.Read(_probe);

        Assert.True(info.ScheduledRestart);
        Assert.True(info.Deadline);
        Assert.True(info.BypassesCoordination());
    }

    [Fact]
    public void PreFlight_WarnsWhenWindowsInstallsOnItsOwn()
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "4";

        var check = Check();

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Contains("group policy", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PreFlight_IsOkWhenWindowsOnlyNotifies()
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "3";

        Assert.Equal(HealthStatus.Ok, Check().Status);
    }

    private HealthCheckResult Check() =>
        new PreFlightCheck(_probe, new FakeDiskSpace(), Options.Create(new UpdateOptions())).Run().Single(c => c.Name == PreFlightCheck.AutomaticUpdatesCheck);
}
