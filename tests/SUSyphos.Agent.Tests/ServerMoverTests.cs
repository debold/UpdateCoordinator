using System.Net;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Trust;
using SUSyphos.Contracts.Agents;
using SUSyphos.Contracts.Trust;
using SUSyphos.TestSupport;

namespace SUSyphos.Agent.Tests;

public sealed class ServerMoverTests : IDisposable
{
    private static readonly Uri Current = new("https://old.example.local/");
    private static readonly Uri Target = new("https://new.example.local:8443/");
    private static readonly AgentIdentity Identity = new(Guid.NewGuid(), "secret");

    private readonly TestAnchor _anchor = new();
    private readonly MemoryTrustStateStore _store = new();
    private readonly FakeTrustBindingFetcher _fetcher = new();
    private readonly FakeMoveTargetProbe _probe = new();
    private readonly FakeAgentAddressStore _addresses = new();
    private readonly FakeServiceRestarter _restarter = new();
    private readonly FakeTimeProvider _time = new();
    private readonly byte[] _oldCertificate = TestAnchor.RandomCertificateHash();

    public ServerMoverTests()
    {
        _store.State = new TrustState(_anchor.PublicKey, 3, Convert.ToBase64String(_oldCertificate), null, false);
    }

    [Fact]
    public async Task SameAnchor_AndKnownAgent_SavesTheAddress_AdoptsTheCertificate_AndRestarts()
    {
        var newCertificate = TestAnchor.RandomCertificateHash();
        _fetcher.Result = (_anchor.Sign(4, newCertificate), newCertificate);
        var (mover, trust) = Create();

        Assert.True(await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken));

        Assert.Equal(Target, _fetcher.Addresses.Single());
        Assert.Equal(Convert.ToBase64String(newCertificate), _probe.LastCertificate);
        Assert.Equal(Target, _addresses.Saved);
        Assert.Equal(Convert.ToBase64String(newCertificate), _store.State.AcceptedCertificateSpkiSha256);
        Assert.Equal(_anchor.PublicKey, _store.State.AnchorPublicKey);
        Assert.Equal(1, _restarter.Requests);
        Assert.Null(mover.LastError);
        using var certificate = TestCertificates.Create();
        Assert.False(trust.IsAccepted(certificate));
    }

    [Fact]
    public async Task SameCertificateAndSerial_IsAccepted()
    {
        _fetcher.Result = (_anchor.Sign(3, _oldCertificate), _oldCertificate);
        var (mover, _) = Create();

        Assert.True(await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken));
        Assert.Equal(Target, _addresses.Saved);
    }

    [Fact]
    public async Task ForeignAnchor_StaysAndReports()
    {
        using var foreign = new TestAnchor();
        var hash = TestAnchor.RandomCertificateHash();
        _fetcher.Result = (foreign.Sign(9, hash), hash);
        var pinned = _store.State;
        var (mover, _) = Create();

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken));

        Assert.Contains("another trust anchor", mover.LastError, StringComparison.Ordinal);
        Assert.Null(_addresses.Saved);
        Assert.Null(_probe.LastCertificate);
        Assert.Equal(pinned, _store.State);
        Assert.Equal(0, _restarter.Requests);
    }

    [Fact]
    public async Task CertificateNotSignedByTheAnchor_StaysAndReports()
    {
        // The binding is genuine (copied from the old server), but the new server presents another certificate.
        _fetcher.Result = (_anchor.Sign(4, _oldCertificate), TestAnchor.RandomCertificateHash());
        var (mover, _) = Create();

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken));
        Assert.Contains("CertificateMismatch", mover.LastError, StringComparison.Ordinal);
        Assert.Null(_addresses.Saved);
    }

    [Fact]
    public async Task OlderBackup_StaysAndReports()
    {
        var hash = TestAnchor.RandomCertificateHash();
        _fetcher.Result = (_anchor.Sign(2, hash), hash);
        var (mover, _) = Create();

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken));
        Assert.Contains("newer backup", mover.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAgent_StaysAndReports()
    {
        _fetcher.Result = (_anchor.Sign(3, _oldCertificate), _oldCertificate);
        _probe.Status = HttpStatusCode.Unauthorized;
        var (mover, _) = Create();

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken));
        Assert.Contains("does not know this agent", mover.LastError, StringComparison.Ordinal);
        Assert.Null(_addresses.Saved);
        Assert.Equal(Convert.ToBase64String(_oldCertificate), _store.State.AcceptedCertificateSpkiSha256);
    }

    [Fact]
    public async Task Unreachable_StaysReports_AndTriesAgainOnlyAfterTheRetryInterval()
    {
        _fetcher.Failure = new HttpRequestException("No such host is known.\r\n");
        var (mover, _) = Create();

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken));
        Assert.Equal("https://new.example.local:8443/ is not reachable: No such host is known.", mover.LastError);

        await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken);
        Assert.Single(_fetcher.Addresses);

        _time.Advance(ServerMover.RetryInterval);
        await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken);
        Assert.Equal(2, _fetcher.Addresses.Count);
    }

    [Fact]
    public async Task AddressSetByPolicy_IsNeverChanged()
    {
        _addresses.IsSetByPolicy = true;
        _fetcher.Result = (_anchor.Sign(3, _oldCertificate), _oldCertificate);
        var (mover, _) = Create();

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken));
        Assert.Contains("group policy", mover.LastError, StringComparison.Ordinal);
        Assert.Empty(_fetcher.Addresses);
    }

    [Theory]
    [InlineData("http://new.example.local/")]
    [InlineData("https://user:pw@new.example.local/")]
    [InlineData("https://new.example.local/?a=b")]
    public async Task InvalidAddress_IsRefused(string address)
    {
        var (mover, _) = Create();

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, Announce(new Uri(address)), TestContext.Current.CancellationToken));
        Assert.Equal("The announced address is not a valid HTTPS address.", mover.LastError);
        Assert.Empty(_fetcher.Addresses);
    }

    [Fact]
    public async Task OwnAddress_OrNoAnnouncement_DoesNothing_AndClearsTheError()
    {
        _fetcher.Failure = new HttpRequestException("down");
        var (mover, _) = Create();
        await mover.MoveIfAnnouncedAsync(Identity, Announce(Target), TestContext.Current.CancellationToken);
        Assert.NotNull(mover.LastError);

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, Announce(new Uri("https://OLD.example.local")), TestContext.Current.CancellationToken));
        Assert.Null(mover.LastError);
        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, new HeartbeatResponse(RegistrationState.Approved), TestContext.Current.CancellationToken));
        Assert.Null(mover.LastError);
        Assert.Single(_fetcher.Addresses);
    }

    [Fact]
    public async Task NotApproved_IgnoresTheAnnouncement()
    {
        var (mover, _) = Create();

        Assert.False(await mover.MoveIfAnnouncedAsync(Identity, new HeartbeatResponse(RegistrationState.Blocked, MoveTo: Target), TestContext.Current.CancellationToken));
        Assert.Empty(_fetcher.Addresses);
    }

    [Fact]
    public void WithoutPinnedAnchor_NoMoveTargetIsAccepted()
    {
        _store.State = TrustState.Empty;
        var (_, trust) = Create();
        var hash = TestAnchor.RandomCertificateHash();

        Assert.Equal(TrustDecision.AnchorMismatch, trust.CheckMoveTarget(_anchor.Sign(1, hash), hash, out _));
    }

    [Fact]
    public void AdoptMoveTarget_RefusesAnotherAnchor()
    {
        var (_, trust) = Create();
        using var foreign = new TestAnchor();

        Assert.Throws<InvalidOperationException>(() => trust.AdoptMoveTarget(_store.State with { AnchorPublicKey = foreign.PublicKey }));
    }

    [Theory]
    [InlineData("https://Updates.Example.local", "https://updates.example.local/")]
    [InlineData("https://updates.example.local:8443/sub", "https://updates.example.local:8443/sub/")]
    [InlineData(" https://10.0.0.5:443/ ", "https://10.0.0.5/")]
    public void Address_IsNormalized(string text, string expected)
    {
        Assert.True(CentralServerAddress.TryNormalize(text, out var address));
        Assert.Equal(expected, address!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("updates.example.local")]
    [InlineData("ftp://updates.example.local/")]
    [InlineData("https://updates.example.local/#x")]
    public void Address_InvalidIsRefused(string? text) => Assert.False(CentralServerAddress.TryNormalize(text, out _));

    public void Dispose() => _anchor.Dispose();

    private static HeartbeatResponse Announce(Uri target) => new(RegistrationState.Approved, MoveTo: target);

    private (ServerMover Mover, ServerTrust Trust) Create()
    {
        var trust = new ServerTrust(_store, _fetcher, Options.Create(new TrustOptions()), NullLogger<ServerTrust>.Instance);
        var mover = new ServerMover(
            trust,
            _fetcher,
            _probe,
            _addresses,
            _restarter,
            Options.Create(new CentralServerOptions { BaseAddress = Current }),
            _time,
            NullLogger<ServerMover>.Instance);
        return (mover, trust);
    }
}
