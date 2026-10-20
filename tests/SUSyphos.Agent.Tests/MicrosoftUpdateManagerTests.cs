using Microsoft.Extensions.Logging.Abstractions;

using SUSyphos.Agent.Configuration;
using SUSyphos.Agent.Updates;

namespace SUSyphos.Agent.Tests;

internal sealed class FakeUpdateServiceRegistry : IUpdateServiceRegistry
{
    public HashSet<string> Registered { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Fail { get; set; }

    public int Calls { get; private set; }

    public bool IsRegistered(string serviceId)
    {
        Calls++;
        return Fail ? throw new UnauthorizedAccessException("WUA unavailable") : Registered.Contains(serviceId);
    }

    public void Register(string serviceId) => Registered.Add(serviceId);

    public void Unregister(string serviceId) => Registered.Remove(serviceId);
}

public sealed class MicrosoftUpdateManagerTests
{
    private readonly FakeSystemProbe _probe = new();
    private readonly FakeUpdateServiceRegistry _services = new();
    private readonly UpdateServiceSelection _selection = new();

    internal static MicrosoftUpdateManager Create(FakeSystemProbe probe, FakeUpdateServiceRegistry services, UpdateServiceSelection selection) =>
        new(services, probe, new FakeRegistryWriter(probe), selection, NullLogger<MicrosoftUpdateManager>.Instance);

    [Fact]
    public void Enabled_RegistersMicrosoftUpdate_MarksIt_AndSearchesIt()
    {
        Create(_probe, _services, _selection).Apply(true);

        Assert.Contains(UpdateServiceSelection.MicrosoftUpdateServiceId, _services.Registered);
        Assert.Equal("1", _probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, MicrosoftUpdateManager.MarkerName));
        Assert.Equal(UpdateServiceSelection.MicrosoftUpdateServiceId, _selection.ServiceId);
    }

    [Fact]
    public void Disabled_RemovesOnlyTheAgentsOwnRegistration()
    {
        // Registered by an administrator (no marker): stays when the option is off.
        _services.Registered.Add(UpdateServiceSelection.MicrosoftUpdateServiceId);
        Create(_probe, _services, _selection).Apply(false);
        Assert.Contains(UpdateServiceSelection.MicrosoftUpdateServiceId, _services.Registered);
        Assert.Null(_selection.ServiceId);

        // Registered by the agent: removed again together with the marker.
        _services.Registered.Clear();
        var manager = Create(_probe, _services, _selection);
        manager.Apply(true);
        manager.Apply(false);
        Assert.Empty(_services.Registered);
        Assert.Null(_probe.GetRegistryValue(AgentRegistry.InstallerKeyPath, MicrosoftUpdateManager.MarkerName));
        Assert.Null(_selection.ServiceId);
    }

    [Fact]
    public void AnUnchangedSetting_DoesNotQueryWindowsUpdateEveryCycle()
    {
        var manager = Create(_probe, _services, _selection);

        manager.Apply(true);
        manager.Apply(true);
        manager.Apply(true);

        Assert.Equal(1, _services.Calls);
    }

    [Fact]
    public void Failure_FallsBackToTheSystemDefault_AndIsRetried()
    {
        _services.Fail = true;
        var manager = Create(_probe, _services, _selection);

        manager.Apply(true);
        Assert.Null(_selection.ServiceId);

        _services.Fail = false;
        manager.Apply(true);
        Assert.Equal(UpdateServiceSelection.MicrosoftUpdateServiceId, _selection.ServiceId);
    }
}
