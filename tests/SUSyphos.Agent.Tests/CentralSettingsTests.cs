using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Agent.Configuration;
using SUSyphos.Agent.Updates;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

public sealed class CentralSettingsTests
{
    private static (ServiceProvider Services, CentralSettingsProvider Provider) Build(IReadOnlyDictionary<string, string>? initial = null)
    {
        var source = new CentralSettingsSource(initial);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Updates:SearchInterval"] = "02:00:00", ["CentralServer:BaseAddress"] = "https://central.example.local/" })
            .Add(source)
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<UpdateOptions>().Bind(configuration.GetSection(UpdateOptions.SectionName)).ValidateDataAnnotations();
        services.AddSingleton<IOptions<UpdateOptions>>(provider => new LiveOptions<UpdateOptions>(provider.GetRequiredService<IOptionsMonitor<UpdateOptions>>()));
        return (services.BuildServiceProvider(), source.Provider);
    }

    [Fact]
    public void CentralValues_OverrideTheLocalFiles_AndApplyAtRunTime()
    {
        var (services, provider) = Build();
        using (services)
        {
            var options = services.GetRequiredService<IOptions<UpdateOptions>>();
            Assert.Equal(TimeSpan.FromHours(2), options.Value.SearchInterval);

            Assert.Equal(["search-interval", "include-drivers"], provider.Apply(new Dictionary<string, string> { ["search-interval"] = "6", ["include-drivers"] = "false" }));
            Assert.Equal(TimeSpan.FromHours(6), options.Value.SearchInterval);
            Assert.False(options.Value.IncludeDrivers);

            // Removed centrally: the local value applies again.
            provider.Apply(new Dictionary<string, string>());
            Assert.Equal(TimeSpan.FromHours(2), options.Value.SearchInterval);
            Assert.True(options.Value.IncludeDrivers);
        }
    }

    [Fact]
    public void UnknownOrInvalidValues_NeverReachTheConfiguration()
    {
        var (services, provider) = Build();
        using (services)
        {
            var changed = provider.Apply(new Dictionary<string, string>
            {
                ["search-interval"] = "99",
                ["min-free-disk"] = "abc",
                ["Updates:Source"] = "WindowsUpdate",
                ["restart-message"] = "Restart \"now\"",
                ["log-level"] = "Trace",
            });

            Assert.Empty(changed);
            Assert.Equal(TimeSpan.FromHours(2), services.GetRequiredService<IOptions<UpdateOptions>>().Value.SearchInterval);
        }
    }

    [Fact]
    public void KeptValues_ApplyFromTheStart()
    {
        var (services, _) = Build(new Dictionary<string, string> { ["search-interval"] = "12" });
        using (services)
        {
            Assert.Equal(TimeSpan.FromHours(12), services.GetRequiredService<IOptions<UpdateOptions>>().Value.SearchInterval);
        }
    }

    [Fact]
    public void LoggingChange_RestartsTheService_OtherChangesDoNot()
    {
        var restarter = new FakeServiceRestarter();
        var settings = new CentralSettings(new CentralSettingsProvider(null), null, restarter, NullLogger<CentralSettings>.Instance);

        settings.Apply(new Dictionary<string, string> { ["heartbeat-interval"] = "30" });
        Assert.False(settings.RestartNeeded);
        Assert.Equal(0, restarter.Requests);

        settings.Apply(new Dictionary<string, string> { ["heartbeat-interval"] = "30", ["log-level"] = "Debug" });
        Assert.True(settings.RestartNeeded);
        Assert.Equal(1, restarter.Requests);
        Assert.Equal("Debug", settings.Current["log-level"]);
    }

    [Fact]
    public async Task ReleaseList_CarriesTheSettingsToTheAgent()
    {
        var client = new FakeCentralServerClient
        {
            ReleaseListResult = new ReleaseList(4, [], AgentSettings: new Dictionary<string, string> { ["heartbeat-interval"] = "60" }),
        };
        var settings = new CentralSettings(new CentralSettingsProvider(null), null, null, NullLogger<CentralSettings>.Instance);
        var sync = new ReleaseSync(client, NullLogger<ReleaseSync>.Instance, settings);

        await sync.SyncAsync(new Identity.AgentIdentity(Guid.NewGuid(), "secret"), new HeartbeatResponse(RegistrationState.Approved, 4, false, 0), TestContext.Current.CancellationToken);

        Assert.Equal("60", settings.Current["heartbeat-interval"]);
    }

    [Theory]
    [InlineData("heartbeat-interval", "30", "00:00:30")]
    [InlineData("search-interval", "6", "06:00:00")]
    [InlineData("restart-warning", "10", "00:10:00")]
    [InlineData("memory-limit", "300", "300")]
    [InlineData("low-priority", "off", "false")]
    public void Definitions_TranslateToTheAgentConfiguration(string key, string value, string expected)
    {
        var definition = AgentSettings.Find(key)!;
        Assert.Equal(expected, definition.ToConfiguration(definition.Normalize(value)!));
        Assert.Equal(definition.Normalize(value), definition.FromConfiguration(expected));
    }

    [Fact]
    public void AppliedReport_NamesTheSource_AndIsSentOnlyWhenItChanged()
    {
        var source = new CentralSettingsSource(new Dictionary<string, string> { ["heartbeat-interval"] = "30", ["log-level"] = "Debug" });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Updates:SearchInterval"] = "02:00:00", ["Updates:MinFreeDiskSpaceMegabytes"] = "4096" })
            .Add(source)
            // A local policy above the central value (like the registry keys).
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:MinimumLevel"] = "Warning" })
            .Build();
        var report = new AppliedSettingsReport(configuration, source.Provider);

        var first = report.Pending()!;
        Assert.Equal(new AppliedAgentSetting("30", AgentSettingSource.Central), first["heartbeat-interval"]);
        Assert.Equal(new AppliedAgentSetting("Warning", AgentSettingSource.Local), first["log-level"]);
        Assert.Equal(new AppliedAgentSetting("2", AgentSettingSource.Local), first["search-interval"]);
        Assert.Equal(new AppliedAgentSetting("4096", AgentSettingSource.AgentDefault), first["min-free-disk"]);
        Assert.Equal(AgentSettings.All.Count, first.Count);

        // Not acknowledged (heartbeat failed): sent again; acknowledged: nothing until it changes.
        Assert.NotNull(report.Pending());
        report.Acknowledge(first);
        Assert.Null(report.Pending());

        source.Provider.Apply(new Dictionary<string, string> { ["heartbeat-interval"] = "60" });
        Assert.Equal("60", report.Pending()!["heartbeat-interval"].Value);
    }
}
