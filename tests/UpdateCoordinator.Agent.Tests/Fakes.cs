using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Tests;

internal sealed class FakeEnvironment : IAgentEnvironment
{
    public string HostName => "SRV01";

    public string? DomainName => "example.local";

    public string OsVersion => "10.0.20348.2700";

    public string AgentVersion => "1.2.3";
}

internal sealed class InMemoryIdentityStore : IAgentIdentityStore
{
    public AgentIdentity? Identity { get; set; }

    public AgentIdentity? Load() => Identity;

    public void Save(AgentIdentity identity) => Identity = identity;

    public void Delete() => Identity = null;
}

internal sealed class FakeCentralServerClient : ICentralServerClient
{
    private int _heartbeats;
    private int _registrations;

    public AgentIdentity IssuedIdentity { get; } = new(Guid.NewGuid(), "issued-secret");

    public Func<AgentIdentity, HeartbeatResponse?> OnHeartbeat { get; set; } = _ => new HeartbeatResponse(RegistrationState.Pending);

    public RegisterRequest? LastRegisterRequest { get; private set; }

    public int Registrations => Volatile.Read(ref _registrations);

    public int Heartbeats => Volatile.Read(ref _heartbeats);

    public Task<AgentIdentity> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        LastRegisterRequest = request;
        Interlocked.Increment(ref _registrations);
        return Task.FromResult(IssuedIdentity);
    }

    public Task<HeartbeatResponse?> HeartbeatAsync(AgentIdentity identity, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _heartbeats);
        return Task.FromResult(OnHeartbeat(identity));
    }
}
