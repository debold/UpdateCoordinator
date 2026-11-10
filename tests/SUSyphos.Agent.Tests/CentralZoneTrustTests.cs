using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Agent.Trust;

namespace SUSyphos.Agent.Tests;

/// <summary>The central server in the intranet zone of SYSTEM, so WinGet can start its installers; nothing here touches the machine.</summary>
public sealed class CentralZoneTrustTests
{
    private sealed class FakeMap : ISystemZoneMap
    {
        public string? AddedHost { get; set; }

        public List<string> Hosts { get; } = [];

        public void AddToIntranet(string host) => Hosts.Add(host);

        public void RemoveFromIntranet(string host) => Hosts.Remove(host);
    }

    private sealed class Monitor(CentralServerOptions value) : IOptionsMonitor<CentralServerOptions>
    {
        public CentralServerOptions CurrentValue { get; set; } = value;

        public CentralServerOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<CentralServerOptions, string?> listener) => null;
    }

    private static (CentralZoneTrust Trust, FakeMap Map, Monitor Options) Create(string address)
    {
        var map = new FakeMap();
        var options = new Monitor(new CentralServerOptions { BaseAddress = new Uri(address) });
        return (new CentralZoneTrust(options, map, NullLogger<CentralZoneTrust>.Instance), map, options);
    }

    [Fact]
    public void CentralServer_IsAddedOnce_AndRemovedWhenSoftwareIsOff()
    {
        var (trust, map, _) = Create("https://LabVmd01.ad.be-ffm.de:8443/");

        trust.Apply(true);
        trust.Apply(true);

        Assert.Equal(["labvmd01.ad.be-ffm.de"], map.Hosts);
        Assert.Equal("labvmd01.ad.be-ffm.de", map.AddedHost);

        trust.Apply(false);
        Assert.Empty(map.Hosts);
        Assert.Null(map.AddedHost);
    }

    [Fact]
    public void NewAddress_ReplacesTheOldEntry()
    {
        var (trust, map, options) = Create("https://old.lab.local/");
        trust.Apply(true);

        options.CurrentValue = new CentralServerOptions { BaseAddress = new Uri("https://new.lab.local/") };
        trust.Apply(true);

        Assert.Equal(["new.lab.local"], map.Hosts);
    }

    [Theory]
    [InlineData("https://central.lab.local/", "central.lab.local")]
    [InlineData("https://10.0.0.5:8443/", "10.0.0.5")]
    [InlineData("https://central/", null)]
    [InlineData("https://[fd00::5]/", null)]
    public void Host_IsANameWithDotsOrAnIPv4Address(string address, string? expected) =>
        Assert.Equal(expected, CentralZoneTrust.Host(new Uri(address)));

    [Fact]
    public void Paths_UseTheDomainHierarchy_OrARangeForAnAddress()
    {
        Assert.Equal(
            [$@"{SystemZoneMap.ZoneMapKey}\Domains\be-ffm.de\labvmd01.ad", $@"{SystemZoneMap.ZoneMapKey}\EscDomains\be-ffm.de\labvmd01.ad"],
            SystemZoneMap.Paths("labvmd01.ad.be-ffm.de").Select(p => p.Key));
        Assert.Equal($@"{SystemZoneMap.ZoneMapKey}\Domains\lab.local", SystemZoneMap.Paths("lab.local").First().Key);
        var range = SystemZoneMap.Paths("10.0.0.5").First();
        Assert.Equal(($@"{SystemZoneMap.ZoneMapKey}\Ranges\{SystemZoneMap.RangeName}", "10.0.0.5"), (range.Key, range.Range));
    }
}