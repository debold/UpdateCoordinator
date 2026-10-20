using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Options;

using UpdateCoordinator.Contracts.Trust;

namespace UpdateCoordinator.Agent.Trust;

/// <summary>
/// Raised when the central server cannot prove its identity; the agent then sends nothing.
/// </summary>
internal sealed class UntrustedServerException(TrustDecision decision)
    : Exception($"The central server is not trusted ({decision}).")
{
    public TrustDecision Decision { get; } = decision;
}

/// <summary>
/// Holds the trust state of the agent and decides which TLS certificate is accepted
/// (see docs/03-communication-protocol.md, section 3.2).
/// </summary>
internal sealed partial class ServerTrust : IServerTrust
{
    private readonly ITrustStateStore _store;
    private readonly ITrustBindingFetcher _fetcher;
    private readonly ILogger<ServerTrust> _logger;
    private readonly string? _expectedFingerprint;
    private readonly Lock _lock = new();
    private TrustState _state;
    private byte[]? _acceptedHash;
    private bool _refreshRequested;

    public ServerTrust(ITrustStateStore store, ITrustBindingFetcher fetcher, IOptions<TrustOptions> options, ILogger<ServerTrust> logger)
    {
        _store = store;
        _fetcher = fetcher;
        _logger = logger;
        _expectedFingerprint = options.Value.AnchorFingerprint;
        _state = store.Load();
        ApplyResetToken(options.Value.ResetToken);
        _acceptedHash = Decode(_state.AcceptedCertificateSpkiSha256);
    }

    /// <summary>True until the central server has acknowledged a heartbeat that reported a local trust reset.</summary>
    public bool ResetNotReported
    {
        get
        {
            lock (_lock)
            {
                return _state.ResetNotReported;
            }
        }
    }

    /// <summary>Certificate check for every connection to the central server.</summary>
    public bool IsAccepted(X509Certificate? certificate)
    {
        var accepted = Volatile.Read(ref _acceptedHash);
        if (certificate is null || accepted is null)
        {
            return false;
        }

        using var x509 = new X509Certificate2(certificate);
        return TrustBindingFormat.ComputeSpkiHash(x509).AsSpan().SequenceEqual(accepted);
    }

    /// <summary>HTTP handler for all messages: accepts only the certificate bound to the pinned anchor, independent of any CA.</summary>
    public SocketsHttpHandler CreateValidatingHandler() => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) => IsAccepted(certificate),
        },
    };

    /// <summary>
    /// Makes sure a certificate is accepted before any message is sent: on first contact, after a reset
    /// or after a rejected certificate, the binding is fetched and verified.
    /// </summary>
    public async Task EnsureTrustedAsync(CancellationToken cancellationToken)
    {
        bool refresh;
        lock (_lock)
        {
            refresh = _refreshRequested || _acceptedHash is null;
        }

        if (!refresh)
        {
            return;
        }

        var (binding, presented) = await _fetcher.FetchAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            var firstContact = _state.AnchorPublicKey is null;
            var decision = TrustBindingVerifier.Verify(binding, presented, _state, out var newState, _expectedFingerprint);
            if (decision != TrustDecision.Accepted)
            {
                LogUntrusted(decision);
                throw new UntrustedServerException(decision);
            }

            _store.Save(newState);
            _state = newState;
            Volatile.Write(ref _acceptedHash, Decode(newState.AcceptedCertificateSpkiSha256));
            _refreshRequested = false;
            LogAccepted(firstContact, newState.HighestSerial);
        }
    }

    /// <summary>Called when the TLS handshake failed; the next cycle fetches a new binding.</summary>
    public void RequestRefresh()
    {
        lock (_lock)
        {
            _refreshRequested = true;
        }
    }

    public void MarkResetReported()
    {
        lock (_lock)
        {
            if (_state.ResetNotReported)
            {
                _state = _state with { ResetNotReported = false };
                _store.Save(_state);
            }
        }
    }

    private void ApplyResetToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token == _state.LastResetToken)
        {
            return;
        }

        // Discard everything that was pinned; the next contact is a first contact again.
        _state = TrustState.Empty with { LastResetToken = token, ResetNotReported = true };
        _store.Save(_state);
        LogReset();
    }

    private static byte[]? Decode(string? value) => value is null ? null : Convert.FromBase64String(value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trust in the central server was reset locally by the registry override; the next contact pins the central server again.")]
    private partial void LogReset();

    [LoggerMessage(Level = LogLevel.Information, Message = "Central server certificate accepted (first contact: {FirstContact}, binding serial {Serial}).")]
    private partial void LogAccepted(bool firstContact, long serial);

    [LoggerMessage(Level = LogLevel.Error, Message = "Central server not trusted ({Decision}); nothing is sent until it presents a valid binding or trust is reset locally.")]
    private partial void LogUntrusted(TrustDecision decision);
}
