using System.ComponentModel.DataAnnotations;

using UpdateCoordinator.Common;

namespace UpdateCoordinator.Server.Trust;

/// <summary>
/// Trust anchor and TLS certificate settings. Bound from the "Trust" configuration section.
/// </summary>
internal sealed class TrustOptions
{
    public const string SectionName = "Trust";

    /// <summary>DPAPI-protected file holding the private key of the trust anchor. Must be included in backups.</summary>
    [Required]
    public string AnchorPath { get; set; } = Path.Combine(DataPaths.Server, "trust-anchor.key");

    /// <summary>
    /// Thumbprint (SHA-1, hex) of the TLS certificate in the Windows certificate store (LocalMachine\My, then CurrentUser\My).
    /// Normally selected in the dashboard (Administration → TLS certificate) or by the installer; both store it in the registry.
    /// In development the ASP.NET Core development certificate is used when no thumbprint is set.
    /// </summary>
    [RegularExpression("^[0-9A-Fa-f]{40}$")]
    public string? CertificateThumbprint { get; set; }
}
