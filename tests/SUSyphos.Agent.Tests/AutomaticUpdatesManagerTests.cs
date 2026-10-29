using Microsoft.Extensions.Logging.Abstractions;

using SUSyphos.Agent.Configuration;
using SUSyphos.Agent.Inventory;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

/// <summary>Writes into the fake probe, so reads see the writes like on a real machine.</summary>
internal sealed class FakeRegistryWriter(FakeSystemProbe probe) : IRegistryWriter
{
    public int Writes { get; private set; }

    public void SetDword(string path, string name, int value)
    {
        Writes++;
        probe.Values[(path, name)] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void SetString(string path, string name, string value)
    {
        Writes++;
        probe.Values[(path, name)] = value;
    }

    public void DeleteValue(string path, string name)
    {
        Writes++;
        probe.Values.Remove((path, name));
    }
}

public sealed class AutomaticUpdatesManagerTests
{
    private readonly FakeSystemProbe _probe = new();
    private readonly FakeRegistryWriter _registry;
    private readonly AutomaticUpdatesManager _manager;

    public AutomaticUpdatesManagerTests()
    {
        _registry = new FakeRegistryWriter(_probe);
        _manager = Create(_probe, _registry);
    }

    internal static AutomaticUpdatesManager Create(FakeSystemProbe probe, FakeRegistryWriter registry) => new(probe, registry, NullLogger<AutomaticUpdatesManager>.Instance);

    [Fact]
    public void Off_ChangesNothing()
    {
        _probe.Values[(AutomaticUpdatesReader.LocalAuPath, "AUOptions")] = "4";

        _manager.Apply(false);

        Assert.Equal(0, _registry.Writes);
    }

    [Fact]
    public void On_TurnsAutomaticUpdatesOffWhereNoGroupPolicyControlsIt_AndOnlyOnce()
    {
        // NoAutoUpdate = 1 (Marc, 2026-10-29): with option 3 Windows still downloaded the update and offered it.
        _probe.Values[(AutomaticUpdatesReader.LocalAuPath, "AUOptions")] = "4";

        _manager.Apply(true);
        _manager.Apply(true);

        Assert.Equal("1", _probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate"));
        Assert.Null(_probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions"));
        Assert.Equal(2, _registry.Writes);
        var info = AutomaticUpdatesReader.Read(_probe);
        Assert.Equal(AutomaticUpdateMode.Disabled, info.Mode);
        Assert.True(info.ManagedByAgent);
        Assert.False(info.BypassesCoordination());
    }

    [Theory]
    [InlineData("AUOptions", "4")]
    [InlineData("AUOptions", "2")]
    [InlineData("NoAutoUpdate", "1")]
    public void On_NeverOverridesGroupPolicy(string name, string value)
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, name)] = value;

        _manager.Apply(true);

        Assert.Equal(0, _registry.Writes);
        Assert.Equal(value, _probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, name));
    }

    [Fact]
    public void On_WithPolicyLettingTheAdministratorChoose_SetsTheValue()
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "5";

        _manager.Apply(true);

        Assert.Equal("1", _probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate"));
    }

    [Fact]
    public void SwitchedOff_RestoresOnlyTheAgentsOwnValue()
    {
        _manager.Apply(true);
        _manager.Apply(false);

        Assert.Null(_probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate"));
        Assert.Null(_probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, AutomaticUpdatesManager.MarkerName));
    }

    [Fact]
    public void SwitchedOff_AfterGroupPolicyTookOver_KeepsThePolicyValue()
    {
        _manager.Apply(true);
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "4";

        _manager.Apply(false);

        Assert.Equal("4", _probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions"));
        Assert.Null(_probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, AutomaticUpdatesManager.MarkerName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptionThreeOfAnEarlierVersion_IsReplaced_OrRemoved(bool manage)
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "3";
        _probe.Values[(AgentRegistry.InstallerKeyPath, AutomaticUpdatesManager.MarkerName)] = "3";

        _manager.Apply(manage);

        Assert.Null(_probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions"));
        Assert.Equal(manage ? "1" : null, _probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "NoAutoUpdate"));
        Assert.Equal(manage, AutomaticUpdatesManager.IsManagedByAgent(_probe));
    }

    [Fact]
    public void TamperedMarker_DoesNotMakeAForeignValueOurs()
    {
        _probe.Values[(AutomaticUpdatesReader.PolicyAuPath, "AUOptions")] = "4";
        _probe.Values[(AgentRegistry.InstallerKeyPath, AutomaticUpdatesManager.MarkerName)] = "4";

        Assert.False(AutomaticUpdatesManager.IsManagedByAgent(_probe));
        _manager.Apply(false);
        Assert.Equal("4", _probe.GetRegistryValue(AutomaticUpdatesReader.PolicyAuPath, "AUOptions"));
    }
}
