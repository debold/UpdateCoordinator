using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Configuration;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.Trust;

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
        services.AddSingleton<IAgentEnvironment, AgentEnvironment>();
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
