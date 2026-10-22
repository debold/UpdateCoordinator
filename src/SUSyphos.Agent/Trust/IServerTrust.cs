using SUSyphos.Contracts.Trust;

namespace SUSyphos.Agent.Trust;

/// <summary>
/// Trust operations the main loop needs; implemented by <see cref="ServerTrust"/>.
/// </summary>
internal interface IServerTrust
{
    /// <summary>True until a heartbeat reporting a local trust reset was acknowledged.</summary>
    bool ResetNotReported { get; }

    /// <summary>Throws <see cref="UntrustedServerException"/> if the central server cannot prove its identity.</summary>
    Task EnsureTrustedAsync(CancellationToken cancellationToken);

    void RequestRefresh();

    void MarkResetReported();

    /// <summary>Verifies the binding of a move target against the pinned anchor without changing anything (docs/03-communication-protocol.md, 3.4).</summary>
    TrustDecision CheckMoveTarget(TrustBinding? binding, byte[] presentedCertificateSpkiSha256, out TrustState newState);

    /// <summary>Takes over the state verified by <see cref="CheckMoveTarget"/> once the move is saved.</summary>
    void AdoptMoveTarget(TrustState state);
}
