namespace UpdateCoordinator.Agent.Trust;

/// <summary>
/// What the agent trusts about the central server. Binary values are Base64 encoded.
/// </summary>
/// <param name="AnchorPublicKey">Pinned public key of the trust anchor; null before the first contact.</param>
/// <param name="HighestSerial">Highest binding serial ever accepted; older bindings are rejected.</param>
/// <param name="AcceptedCertificateSpkiSha256">Key hash of the TLS certificate currently accepted.</param>
/// <param name="LastResetToken">Last processed value of the registry override, so a reset happens only once per change.</param>
/// <param name="ResetNotReported">True until the central server has been told about a local trust reset.</param>
internal sealed record TrustState(
    string? AnchorPublicKey,
    long HighestSerial,
    string? AcceptedCertificateSpkiSha256,
    string? LastResetToken,
    bool ResetNotReported)
{
    public static TrustState Empty { get; } = new(null, 0, null, null, false);
}
