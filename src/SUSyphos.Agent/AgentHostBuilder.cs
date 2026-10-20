using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

using SUSyphos.Agent.Actions;
using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Configuration;
using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Installation;
using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Trust;
using SUSyphos.Agent.Updates;
using SUSyphos.Common;
using SUSyphos.Common.Logging;

namespace SUSyphos.Agent;

/// <summary>
/// Builds the agent host. Kept separate from Program so the composition can be tested.
/// </summary>
internal static class AgentHostBuilder
{
    public const string ServiceName = "SUSyphos.Agent";

    public static HostApplicationBuilder Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddAgentRegistry(Registry.LocalMachine);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        var logDirectory = builder.Configuration[$"{FileLoggingOptions.SectionName}:Directory"] is { Length: > 0 } configured ? configured : Path.Combine(DataPaths.Agent, "Logs");
        builder.Services.AddRollingFileLogging(builder.Configuration, Path.Combine(DataPaths.Agent, "Logs"), "agent");
        builder.Services.AddSingleton<ILogCollector>(services => new LogCollector(logDirectory, services.GetRequiredService<TimeProvider>()));
        builder.Services.AddAgent(builder.Configuration);
        return builder;
    }

    public static IServiceCollection AddAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CentralServerOptions>()
            .Bind(configuration.GetSection(CentralServerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISystemProbe, WindowsSystemProbe>();
        services.AddSingleton<IAgentEnvironment, AgentEnvironment>();

        services.AddOptions<InventoryOptions>()
            .Bind(configuration.GetSection(InventoryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IInstalledUpdateSource, WuaInstalledUpdateSource>();
        services.AddSingleton<RoleDetector>();
        services.AddSingleton<InventoryCollector>();
        services.AddSingleton<InventoryScheduler>();
        services.AddSingleton<IInventoryReporter, InventoryReporter>();
        services.AddSingleton<IRegistryWriter, WindowsRegistryWriter>();
        services.AddSingleton<AutomaticUpdatesManager>();

        services.AddOptions<UpdateOptions>()
            .Bind(configuration.GetSection(UpdateOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IDiskSpace, DiskSpace>();
        services.AddSingleton<PreFlightCheck>();
        services.AddSingleton<IAvailableUpdateSearch, WuaAvailableUpdateSearch>();
        services.AddSingleton<IUpdateReporter, UpdateReporter>();
        services.AddSingleton<IReleaseSync, ReleaseSync>();

        services.AddOptions<InstallationOptions>()
            .Bind(configuration.GetSection(InstallationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IUpdateInstaller, WuaUpdateInstaller>();
        services.AddSingleton<IInstallationStateStore>(provider => new ProtectedInstallationStateStore(
            ProtectedInstallationStateStore.DefaultPath,
            provider.GetRequiredService<ILogger<ProtectedInstallationStateStore>>()));
        services.AddSingleton<ISystemRestart, ShutdownToolRestart>();
        services.AddSingleton<IServiceRestarter, HostServiceRestarter>();
        services.AddOptions<ResourceOptions>()
            .Bind(configuration.GetSection(ResourceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IProcessResources, CurrentProcessResources>();
        services.AddSingleton<ResourceGuard>();
        services.AddSingleton<IActionRunner, ActionRunner>();
        services.AddSingleton<IInstallationRunner, InstallationRunner>();
        services.AddSingleton<IAgentIdentityStore>(provider => new DpapiAgentIdentityStore(
            DpapiAgentIdentityStore.DefaultPath,
            provider.GetRequiredService<ILogger<DpapiAgentIdentityStore>>()));

        services.AddOptions<TrustOptions>().Bind(configuration.GetSection(TrustOptions.SectionName));
        services.AddSingleton<ITrustStateStore>(provider => new ProtectedTrustStateStore(
            ProtectedTrustStateStore.DefaultPath,
            provider.GetRequiredService<ILogger<ProtectedTrustStateStore>>()));
        services.AddSingleton<ITrustBindingFetcher, TrustBindingFetcher>();
        services.AddSingleton<ServerTrust>();
        services.AddSingleton<IServerTrust>(provider => provider.GetRequiredService<ServerTrust>());

        services.AddHttpClient<ICentralServerClient, CentralServerClient>((provider, http) =>
                http.BaseAddress = provider.GetRequiredService<IOptions<CentralServerOptions>>().Value.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ServerTrust>().CreateValidatingHandler())
            .AddStandardResilienceHandler(resilience =>
            {
                // Register is not idempotent and heartbeats repeat anyway, so the main loop handles retries
                // with its own back-off; this handler contributes timeouts and the circuit breaker.
                resilience.Retry.DisableForUnsafeHttpMethods();
            });

        services.AddOptions<SelfUpdate.AgentUpdateOptions>()
            .Bind(configuration.GetSection(SelfUpdate.AgentUpdateOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<SelfUpdate.IAgentUpdateStateStore>(_ => new SelfUpdate.JsonAgentUpdateStateStore(SelfUpdate.JsonAgentUpdateStateStore.DefaultPath));
        services.AddSingleton<SelfUpdate.IPackageInstaller, SelfUpdate.MsiexecInstaller>();
        services.AddSingleton<SelfUpdate.AgentSelfUpdater>();
        // Same trust check as every message, but streamed and with a timeout long enough for a package over a slow link.
        services.AddHttpClient<SelfUpdate.IAgentPackageClient, SelfUpdate.AgentPackageClient>((provider, http) =>
            {
                http.BaseAddress = provider.GetRequiredService<IOptions<CentralServerOptions>>().Value.BaseAddress;
                http.Timeout = TimeSpan.FromMinutes(30);
            })
            .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ServerTrust>().CreateValidatingHandler());

        services.AddHostedService<AgentWorker>();
        return services;
    }
}
