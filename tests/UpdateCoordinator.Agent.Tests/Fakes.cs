using UpdateCoordinator.Agent.Communication;
using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Agent.Inventory;
using UpdateCoordinator.Agent.Trust;
using UpdateCoordinator.Agent.Updates;
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

    public Action<HeartbeatRequest> OnHeartbeatRequest { get; set; } = _ => { };

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
        OnHeartbeatRequest(request);
        return Task.FromResult(OnHeartbeat(identity));
    }

    public List<InventoryRequest> Inventories { get; } = [];

    public bool AcceptInventory { get; set; } = true;

    public Task<bool> SendInventoryAsync(AgentIdentity identity, InventoryRequest request, CancellationToken cancellationToken)
    {
        Inventories.Add(request);
        return Task.FromResult(AcceptInventory);
    }

    public List<UpdateReport> Reports { get; } = [];

    public bool AcceptReports { get; set; } = true;

    public ReleaseList? ReleaseListResult { get; set; } = new(0, []);

    public int ReleaseRequests { get; private set; }

    public Task<bool> SendUpdateReportAsync(AgentIdentity identity, UpdateReport report, CancellationToken cancellationToken)
    {
        Reports.Add(report);
        return Task.FromResult(AcceptReports);
    }

    public Task<ReleaseList?> GetReleasesAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        ReleaseRequests++;
        return Task.FromResult(ReleaseListResult);
    }
}

internal sealed class FakeUpdateReporter : IUpdateReporter
{
    public bool Accept { get; set; } = true;

    public int Runs { get; private set; }

    public Task<bool> RunIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        Runs++;
        return Task.FromResult(Accept);
    }
}

internal sealed class FakeReleaseSync : IReleaseSync
{
    public bool Accept { get; set; } = true;

    public List<long?> AnnouncedVersions { get; } = [];

    public ReleaseList Current { get; } = new(0, []);

    public Task<bool> SyncAsync(AgentIdentity identity, long? announcedVersion, CancellationToken cancellationToken)
    {
        AnnouncedVersions.Add(announcedVersion);
        return Task.FromResult(Accept);
    }
}

internal sealed class FakeInventoryReporter : IInventoryReporter
{
    public bool Accept { get; set; } = true;

    public int Reports { get; private set; }

    public int Resets { get; private set; }

    public Task<bool> ReportIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        Reports++;
        return Task.FromResult(Accept);
    }

    public void Reset() => Resets++;
}

internal sealed class FakeServerTrust : IServerTrust
{
    public bool Trusted { get; set; } = true;

    public bool ResetNotReported { get; set; }

    public int RefreshRequests { get; private set; }

    public Task EnsureTrustedAsync(CancellationToken cancellationToken) =>
        Trusted ? Task.CompletedTask : throw new UntrustedServerException(TrustDecision.InvalidSignature);

    public void RequestRefresh() => RefreshRequests++;

    public void MarkResetReported() => ResetNotReported = false;
}
