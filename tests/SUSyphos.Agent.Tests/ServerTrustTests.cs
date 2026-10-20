using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SUSyphos.Agent.Trust;
using SUSyphos.Contracts.Trust;
using SUSyphos.TestSupport;

namespace SUSyphos.Agent.Tests;

public sealed class ServerTrustTests : IDisposable
{
    private readonly TestAnchor _anchor = new();
    private readonly InMemoryTrustStateStore _store = new();
    private readonly FakeBindingFetcher _fetcher = new();

    [Fact]
    public async Task EnsureTrusted_OnFirstContact_PinsAndPersists()
    {
        var hash = TestAnchor.RandomCertificateHash();
        _fetcher.Result = (_anchor.Sign(1, hash), hash);
        var trust = Create();

        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_anchor.PublicKey, _store.State.AnchorPublicKey);
        Assert.Equal(1, _fetcher.Calls);
    }

    [Fact]
    public async Task EnsureTrusted_WhenAlreadyAccepted_DoesNotFetchAgain()
    {
        var hash = TestAnchor.RandomCertificateHash();
        _fetcher.Result = (_anchor.Sign(1, hash), hash);
        var trust = Create();
        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);

        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _fetcher.Calls);
    }

    [Fact]
    public async Task EnsureTrusted_AfterRefreshRequest_FetchesAgain()
    {
        var hash = TestAnchor.RandomCertificateHash();
        _fetcher.Result = (_anchor.Sign(1, hash), hash);
        var trust = Create();
        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);

        trust.RequestRefresh();
        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _fetcher.Calls);
    }

    [Fact]
    public async Task EnsureTrusted_WithForeignAnchor_ThrowsAndKeepsState()
    {
        var hash = TestAnchor.RandomCertificateHash();
        _fetcher.Result = (_anchor.Sign(1, hash), hash);
        var trust = Create();
        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);
        var pinned = _store.State;

        using var attacker = new TestAnchor();
        var attackerHash = TestAnchor.RandomCertificateHash();
        _fetcher.Result = (attacker.Sign(9, attackerHash), attackerHash);
        trust.RequestRefresh();

        var exception = await Assert.ThrowsAsync<UntrustedServerException>(() => trust.EnsureTrustedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TrustDecision.AnchorMismatch, exception.Decision);
        Assert.Equal(pinned, _store.State);
    }

    [Fact]
    public void ResetToken_DiscardsPinnedAnchorOnce()
    {
        _store.State = new TrustState(_anchor.PublicKey, 7, Convert.ToBase64String(TestAnchor.RandomCertificateHash()), null, false);

        var trust = Create(resetToken: "2026-10-18");

        Assert.Null(_store.State.AnchorPublicKey);
        Assert.Equal("2026-10-18", _store.State.LastResetToken);
        Assert.True(trust.ResetNotReported);

        // The same token after a restart (e.g. rewritten by group policy) must not reset again.
        _store.State = _store.State with { AnchorPublicKey = _anchor.PublicKey, ResetNotReported = false };
        Create(resetToken: "2026-10-18");
        Assert.Equal(_anchor.PublicKey, _store.State.AnchorPublicKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyResetToken_DoesNothing(string? token)
    {
        _store.State = new TrustState(_anchor.PublicKey, 7, null, null, false);

        Create(resetToken: token);

        Assert.Equal(_anchor.PublicKey, _store.State.AnchorPublicKey);
    }

    [Fact]
    public void MarkResetReported_ClearsFlag()
    {
        var trust = Create(resetToken: "reset-1");

        trust.MarkResetReported();

        Assert.False(trust.ResetNotReported);
        Assert.False(_store.State.ResetNotReported);
    }

    [Fact]
    public void IsAccepted_WithoutPinnedCertificate_IsFalse()
    {
        using var certificate = TestCertificates.Create();

        Assert.False(Create().IsAccepted(certificate));
        Assert.False(Create().IsAccepted(null));
    }

    [Fact]
    public async Task IsAccepted_OnlyForPinnedCertificate()
    {
        using var accepted = TestCertificates.Create();
        using var other = TestCertificates.Create();
        var hash = TrustBindingFormat.ComputeSpkiHash(accepted);
        _fetcher.Result = (_anchor.Sign(1, hash), hash);
        var trust = Create();
        await trust.EnsureTrustedAsync(TestContext.Current.CancellationToken);

        Assert.True(trust.IsAccepted(accepted));
        Assert.False(trust.IsAccepted(other));
    }

    private ServerTrust Create(string? resetToken = null) => new(
        _store,
        _fetcher,
        Options.Create(new TrustOptions { ResetToken = resetToken }),
        NullLogger<ServerTrust>.Instance);

    public void Dispose() => _anchor.Dispose();

    private sealed class InMemoryTrustStateStore : ITrustStateStore
    {
        public TrustState State { get; set; } = TrustState.Empty;

        public TrustState Load() => State;

        public void Save(TrustState state) => State = state;
    }

    private sealed class FakeBindingFetcher : ITrustBindingFetcher
    {
        public (TrustBinding? Binding, byte[] Hash) Result { get; set; }

        public int Calls { get; private set; }

        public Task<(TrustBinding? Binding, byte[] PresentedCertificateSpkiSha256)> FetchAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
