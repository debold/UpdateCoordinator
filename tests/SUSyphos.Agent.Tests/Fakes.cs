using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Installation;
using SUSyphos.Agent.Inventory;
using SUSyphos.Agent.Trust;
using SUSyphos.Agent.Updates;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Tests;

internal sealed class FakeEnvironment : IAgentEnvironment
{
    public string HostName => "SRV01";

    public string? DomainName => "example.local";

    public string OsVersion => "10.0.20348.2700";

    public string AgentVersion { get; init; } = "1.2.3";

    public bool RebootPending { get; set; }

    public DateTimeOffset LastBootAt { get; set; } = new(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);
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

    public List<InstallReport> InstallReports { get; } = [];

    public Func<InstallReport, bool> OnInstallReport { get; set; } = _ => true;

    public Task<bool> SendInstallReportAsync(AgentIdentity identity, InstallReport report, CancellationToken cancellationToken)
    {
        var accepted = OnInstallReport(report);
        if (accepted)
        {
            InstallReports.Add(report);
        }

        return Task.FromResult(accepted);
    }

    public AgentActionList? ActionList { get; set; } = new([]);

    public Dictionary<Guid, AgentActionResult> ActionResults { get; } = [];

    public bool AcceptActionResults { get; set; } = true;

    public Task<AgentActionList?> GetActionsAsync(AgentIdentity identity, CancellationToken cancellationToken) => Task.FromResult(ActionList);

    public LeaseResponse? LeaseAnswer { get; set; } = new(true, DateTimeOffset.UtcNow.AddHours(2));

    public int LeaseRequests { get; private set; }

    public List<bool> LeaseReleases { get; } = [];

    public Task<LeaseResponse?> AcquireLeaseAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        LeaseRequests++;
        return Task.FromResult(LeaseAnswer);
    }

    public Task<bool> ReleaseLeaseAsync(AgentIdentity identity, bool succeeded, CancellationToken cancellationToken)
    {
        LeaseReleases.Add(succeeded);
        return Task.FromResult(true);
    }

    public Dictionary<Guid, byte[]> UploadedLogs { get; } = [];

    public Task<bool> UploadLogsAsync(AgentIdentity identity, Guid actionId, byte[] archive, CancellationToken cancellationToken)
    {
        if (AcceptActionResults)
        {
            UploadedLogs[actionId] = archive;
        }

        return Task.FromResult(AcceptActionResults);
    }

    public Task<bool> SendActionResultAsync(AgentIdentity identity, Guid actionId, AgentActionResult result, CancellationToken cancellationToken)
    {
        if (AcceptActionResults)
        {
            ActionResults[actionId] = result;
        }

        return Task.FromResult(AcceptActionResults);
    }
}

internal sealed class FakeUpdateReporter : IUpdateReporter
{
    public bool Accept { get; set; } = true;

    public int Runs { get; private set; }

    public int RunRequests { get; private set; }

    public IReadOnlySet<(Guid UpdateId, int Revision)>? LastMissingUpdates { get; set; }

    public void RequestRun() => RunRequests++;

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

    public ReleaseList Current { get; set; } = new(0, []);

    public bool InstallationsPaused { get; set; }

    public Task<bool> SyncAsync(AgentIdentity identity, HeartbeatResponse heartbeat, CancellationToken cancellationToken)
    {
        AnnouncedVersions.Add(heartbeat.ReleasesVersion);
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

internal sealed class FakeInstallationRunner : IInstallationRunner
{
    public bool Accept { get; set; } = true;

    public int Runs { get; private set; }

    public Task<bool> RunIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        Runs++;
        return Task.FromResult(Accept);
    }
}

internal sealed class FakeRestart : SUSyphos.Agent.Installation.ISystemRestart
{
    public List<TimeSpan> Requests { get; } = [];

    public bool Accept { get; set; } = true;

    public bool Schedule(TimeSpan warning)
    {
        Requests.Add(warning);
        return Accept;
    }
}

internal sealed class FakeServiceRestarter : SUSyphos.Agent.Actions.IServiceRestarter
{
    public int Requests { get; private set; }

    public void RequestRestart() => Requests++;
}

internal sealed class FakeLogCollector : SUSyphos.Agent.Actions.ILogCollector
{
    public (byte[] Archive, int Files) Result { get; set; } = ([0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 0);

    public Exception? Failure { get; set; }

    public (byte[] Archive, int Files) Collect() => Failure is null ? Result : throw Failure;
}

internal sealed class FakeProcessResources : IProcessResources
{
    /// <summary>Memory reported before and after a garbage collection.</summary>
    public long PrivateMemoryBytes { get; set; } = 50L * 1024 * 1024;

    public long? AfterCollection { get; set; }

    public int Collections { get; private set; }

    public bool PriorityLowered { get; private set; }

    public Exception? PriorityFailure { get; set; }

    public void CollectGarbage()
    {
        Collections++;
        if (AfterCollection is { } after)
        {
            PrivateMemoryBytes = after;
        }
    }

    public void LowerPriority()
    {
        if (PriorityFailure is not null)
        {
            throw PriorityFailure;
        }

        PriorityLowered = true;
    }
}
