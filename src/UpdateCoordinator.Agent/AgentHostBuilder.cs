using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

using UpdateCoordinator.Agent.Actions;
using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Configuration;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.Installation;
using UpdateCoordinator.Agent.Inventory;
using UpdateCoordinator.Agent.Trust;
using UpdateCoordinator.Agent.Updates;
using UpdateCoordinator.Common;
using UpdateCoordinator.Common.Logging;

namespace UpdateCoordinator.Agent;

/// <summary>
/// Builds the agent host. Kept separate from Program so the composition can be tested.
/// </summary>
internal static class AgentHostBuilder
{
    public const string ServiceName = "UpdateCoordinator.Agent";

    public static HostApplicationBuilder Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddAgentRegistry(Registry.LocalMachine);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        builder.Services.AddRollingFileLogging(builder.Configuration, Path.Combine(DataPaths.Agent, "Logs"), "agent");
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

        services.AddHostedService<AgentWorker>();
        return services;
    }
}
