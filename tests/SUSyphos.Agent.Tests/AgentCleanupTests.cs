using Microsoft.Extensions.DependencyInjection;

using SUSyphos.Agent.Configuration;
using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Updates;

namespace SUSyphos.Agent.Tests;

/// <summary>"--cleanup" at uninstallation takes back only the agent's own changes (Marc, 2026-10-28).</summary>
public sealed class AgentCleanupTests
{
    [Fact]
    public void Cleanup_RemovesTheAgentsOwnSettings_AndKeepsForeignOnes()
    {
        var probe = new FakeSystemProbe();
        var registry = new FakeRegistryWriter(probe);
        var services = new FakeUpdateServiceRegistry();
        var automatic = AutomaticUpdatesManagerTests.Create(probe, registry);
        automatic.Apply(true);
        services.Registered.Add(UpdateServiceSelection.MicrosoftUpdateServiceId);
        probe.Values[(AgentRegistry.InstallerKeyPath, MicrosoftUpdateManager.MarkerName)] = "1";
        probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "NoAutoRebootWithLoggedOnUsers")] = "1";
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(automatic)
            .AddSingleton(MicrosoftUpdateManagerTests.Create(probe, services, new UpdateServiceSelection()))
            .BuildServiceProvider();

        Assert.Equal(0, AgentCleanup.Run(provider));

        Assert.Null(probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate"));
        Assert.Null(probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, AutomaticUpdatesManager.MarkerName));
        Assert.DoesNotContain(UpdateServiceSelection.MicrosoftUpdateServiceId, services.Registered);
        Assert.Equal("1", probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoRebootWithLoggedOnUsers"));
    }

    [Fact]
    public void Cleanup_NeverFails()
    {
        var probe = new FakeSystemProbe();
        var services = new FakeUpdateServiceRegistry { Fail = true };
        probe.Values[(AgentRegistry.InstallerKeyPath, MicrosoftUpdateManager.MarkerName)] = "1";
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(AutomaticUpdatesManagerTests.Create(probe, new FakeRegistryWriter(probe)))
            .AddSingleton(MicrosoftUpdateManagerTests.Create(probe, services, new UpdateServiceSelection()))
            .BuildServiceProvider();

        Assert.Equal(0, AgentCleanup.Run(provider));
    }
}
