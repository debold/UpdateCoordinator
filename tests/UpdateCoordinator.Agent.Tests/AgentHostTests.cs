using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace UpdateCoordinator.Agent.Tests;

public sealed class AgentHostTests
{
    [Fact]
    public void Create_RegistersAgentWorker()
    {
        var builder = AgentHostBuilder.Create([]);

        using var host = builder.Build();

        Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is AgentWorker);
    }

    [Fact]
    public async Task Worker_StopsCleanly()
    {
        using var worker = new AgentWorker(NullLogger<AgentWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(worker.ExecuteTask);
        Assert.True(worker.ExecuteTask.IsCompleted);
        Assert.False(worker.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task Worker_WithCancelledToken_DoesNotThrow()
    {
        using var worker = new AgentWorker(NullLogger<AgentWorker>.Instance);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await worker.StartAsync(cancelled.Token);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(worker.ExecuteTask is null || !worker.ExecuteTask.IsFaulted);
    }
}
