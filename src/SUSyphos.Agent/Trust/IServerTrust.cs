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
}
