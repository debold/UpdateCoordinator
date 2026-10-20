using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Agent;
using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Trust;
using SUSyphos.Contracts.Agents;
using SUSyphos.TestSupport;

namespace SUSyphos.IntegrationTests;

/// <summary>
/// The real agent trust stack against the central server on a real TLS endpoint.
/// </summary>
public sealed class TlsTrustTests : IAsyncDisposable
{
    private readonly KestrelServerFactory _server = new();
    private readonly InMemoryTrustStore _store = new();

    private static RegisterRequest Register => new()
    {
        HostName = "SRV-TLS",
        OsVersion = "10.0.20348.2700",
        AgentVersion = "1.0.0",
    };

    [Fact]
    public async Task Agent_PinsServerAndCommunicatesOverTls()
    {
        var (trust, client) = CreateAgent(_server.BaseAddress);

        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);
        var identity = await client.RegisterAsync(Register, TestContext.Current.CancellationToken);
        var heartbeat = await client.HeartbeatAsync(identity, new HeartbeatRequest { AgentVersion = "1.0.0" }, TestContext.Current.CancellationToken);

        Assert.Equal(RegistrationState.Pending, heartbeat?.State);
    }

    [Fact]
    public async Task Agent_WithoutTrust_RejectsServerCertificate()
    {
        var (_, client) = CreateAgent(_server.BaseAddress);

        // No binding verified yet: the handshake itself fails, nothing is sent.
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterAsync(Register, TestContext.Current.CancellationToken));
        Assert.IsType<System.Security.Authentication.AuthenticationException>(exception.InnerException);
    }

    [Fact]
    public async Task Agent_FollowsApprovedCertificateChange()
    {
        var (trust, client) = CreateAgent(_server.BaseAddress);
        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);
        var identity = await client.RegisterAsync(Register, TestContext.Current.CancellationToken);

        using var renewed = TestCertificates.Create();
        await _server.ReplaceCertificateAsync(renewed);

        // A fresh agent connection sees the new certificate, the handshake fails, the agent refreshes the binding.
        var (_, freshClient) = CreateAgent(_server.BaseAddress, trust);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            freshClient.HeartbeatAsync(identity, new HeartbeatRequest { AgentVersion = "1.0.0" }, TestContext.Current.CancellationToken));
        trust.RequestRefresh();
        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);

        var heartbeat = await freshClient.HeartbeatAsync(identity, new HeartbeatRequest { AgentVersion = "1.0.0" }, TestContext.Current.CancellationToken);
        Assert.Equal(RegistrationState.Pending, heartbeat?.State);
        Assert.Equal(2, _store.State.HighestSerial);
    }

    [Fact]
    public async Task Agent_PinnedToServer_RejectsImpostorWithOtherAnchor()
    {
        var (trust, _) = CreateAgent(_server.BaseAddress);
        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);

        await using var impostor = new KestrelServerFactory();
        var (impostorTrust, impostorClient) = CreateAgent(impostor.BaseAddress, store: _store);

        // The impostor's certificate is not the pinned one: the handshake fails before anything is sent.
        await Assert.ThrowsAsync<HttpRequestException>(() => impostorClient.RegisterAsync(Register, TestContext.Current.CancellationToken));

        // The agent then fetches the impostor's binding, which is signed by a different anchor.
        impostorTrust.RequestRefresh();
        var exception = await Assert.ThrowsAsync<UntrustedServerException>(() => impostorTrust.EnsureTrustedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TrustDecision.AnchorMismatch, exception.Decision);
        await Assert.ThrowsAsync<HttpRequestException>(() => impostorClient.RegisterAsync(Register, TestContext.Current.CancellationToken));
    }

    private (ServerTrust Trust, CentralServerClient Client) CreateAgent(Uri baseAddress, ServerTrust? existingTrust = null, ITrustStateStore? store = null)
    {
        var options = Options.Create(new CentralServerOptions { BaseAddress = baseAddress });
        var trust = existingTrust ?? new ServerTrust(
            store ?? _store,
            new TrustBindingFetcher(options),
            Options.Create(new TrustOptions()),
            NullLogger<ServerTrust>.Instance);

        // Each test client gets its own connection pool, like a restarted agent.
        var http = new HttpClient(trust.CreateValidatingHandler()) { BaseAddress = baseAddress };
        return (trust, new CentralServerClient(http));
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private sealed class InMemoryTrustStore : ITrustStateStore
    {
        public TrustState State { get; private set; } = TrustState.Empty;

        public TrustState Load() => State;

        public void Save(TrustState state) => State = state;
    }
}
