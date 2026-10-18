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
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        builder.Services.AddHostedService<AgentWorker>();
        return builder;
    }
}
