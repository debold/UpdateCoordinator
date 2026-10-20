namespace UpdateCoordinator.Agent.Trust;

/// <summary>
/// Local trust settings. Bound from the "Trust" configuration section, normally filled from the registry.
/// </summary>
internal sealed class TrustOptions
{
    public const string SectionName = "Trust";

    /// <summary>
    /// Registry override (value TrustResetToken): whenever this value changes, the agent discards the
    /// pinned trust anchor once and pins the central server again on the next contact.
    /// </summary>
    public string? ResetToken { get; set; }

    /// <summary>
    /// Fingerprint of the trust anchor from the installer file name (registry value TrustAnchorFingerprint): when set, the agent
    /// pins only an anchor with this fingerprint. Applies to every pinning, also after a reset.
    /// </summary>
    public string? AnchorFingerprint { get; set; }
}
