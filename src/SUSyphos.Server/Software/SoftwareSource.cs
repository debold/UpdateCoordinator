using SUSyphos.Server.Data;

namespace SUSyphos.Server.Software;

/// <summary>
/// Whether the WinGet source of the central server offers a software version, and why not (lab, Marc 2026-11-20: a
/// version released to every ring stayed "queued" on its servers without a word while its virus scan failed). Servers
/// only get a version the source offers (cached and scanned clean, see the release planner).
/// </summary>
internal static class SoftwareSource
{
    /// <summary>Why the source does not offer the version, in a few words; null when it offers it.</summary>
    public static string? NotOfferedReason(PackageVersionEntity version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.CachedAtUtc is not null ? null
            : version.ScanState switch
            {
                InstallerScanState.Detected => "a virus scan found a threat in the installer",
                InstallerScanState.Failed => "the virus scan of the installer failed",
                _ => "download and virus scan pending",
            };
    }

    /// <summary>The banner text of the update page: why the servers wait and what happens next.</summary>
    public static string Explain(PackageVersionEntity version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.ScanState switch
        {
            InstallerScanState.Detected => $"The virus scan found a threat in the installer ({version.ScanDetail ?? "no name given"}); the version is not offered and no server installs it, also not after a release to every ring. Block the version.",
            InstallerScanState.Failed => $"The virus scan of the installer failed ({version.ScanDetail ?? "no reason given"}); the version is not offered, so no server installs it, also not after a release to every ring. The central server tries again every few minutes and releases the version to the servers as soon as a scan succeeds.",
            _ => "The installer is being downloaded and scanned for viruses; the servers get the version as soon as the WinGet source of the central server offers it.",
        };
    }
}
