using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.TestSupport;

namespace UpdateCoordinator.IntegrationTests;

/// <summary>
/// The real agent HTTP client talking to the real central server pipeline (in memory).
/// </summary>
public sealed class AgentServerTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly HeartbeatRequest Heartbeat = new() { AgentVersion = "1.0.0" };

    private static RegisterRequest Register => new()
    {
        HostName = "SRV-E2E",
        DomainName = "example.local",
        OsVersion = "10.0.20348.2700",
        AgentVersion = "1.0.0",
    };

    [Fact]
    public async Task Agent_RegistersAndReceivesStateViaHeartbeat()
    {
        var client = CreateAgentClient();

        var identity = await client.RegisterAsync(Register, TestContext.Current.CancellationToken);
        var pending = await client.HeartbeatAsync(identity, Heartbeat, TestContext.Current.CancellationToken);

        Assert.Equal(RegistrationState.Pending, pending?.State);

        await SetStateAsync(identity.ClientId, RegistrationState.Approved);
        var approved = await client.HeartbeatAsync(identity, Heartbeat, TestContext.Current.CancellationToken);

        Assert.Equal(RegistrationState.Approved, approved?.State);
    }

    [Fact]
    public async Task Agent_WithDeletedClient_IsToldToRegisterAgain()
    {
        var client = CreateAgentClient();
        var identity = await client.RegisterAsync(Register, TestContext.Current.CancellationToken);
        await factory.WithDbAsync(db => db.Clients
            .Where(c => c.Id == identity.ClientId)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        var response = await client.HeartbeatAsync(identity, Heartbeat, TestContext.Current.CancellationToken);

        Assert.Null(response);
    }

    [Fact]
    public async Task Agent_WithForgedCredentials_IsRejected()
    {
        var client = CreateAgentClient();
        var genuine = await client.RegisterAsync(Register, TestContext.Current.CancellationToken);

        var response = await client.HeartbeatAsync(genuine with { ClientSecret = "forged" }, Heartbeat, TestContext.Current.CancellationToken);

        Assert.Null(response);
    }

    [Fact]
    public async Task Agent_WithInvalidRegistration_Throws()
    {
        var client = CreateAgentClient();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.RegisterAsync(Register with { HostName = "invalid_name" }, TestContext.Current.CancellationToken));
    }

    private CentralServerClient CreateAgentClient() => new(factory.CreateClient());

    private Task<int> SetStateAsync(Guid clientId, RegistrationState state) =>
        factory.WithDbAsync(db => db.Clients
            .Where(c => c.Id == clientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, state), TestContext.Current.CancellationToken));
}
