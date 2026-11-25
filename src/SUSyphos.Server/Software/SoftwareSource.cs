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
                _ when version.ScanDetail?.StartsWith("Download failed", StringComparison.Ordinal) == true => "the download of the installer failed, it is tried again",
                _ => "download and virus scan pending",
            };
    }

    /// <summary>
    /// The banner text of the update page: why the servers wait and what happens next, with the step the installer cache
    /// is in (downloading with the megabytes, scanning), a failed download with its reason, or when the cache runs next.
    /// </summary>
    public static string Explain(PackageVersionEntity version, CacheStep? step = null, DateTime? nextRunUtc = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (step is { Kind: CacheStepKind.Downloading } download)
        {
            var amount = download.TotalBytes is > 0 ? $": {Megabytes(download.Bytes)} of {Megabytes(download.TotalBytes.Value)} MB" : download.Bytes > 0 ? $": {Megabytes(download.Bytes)} MB" : string.Empty;
            return $"The installer is being downloaded since {Components.DisplayTime.Format(download.SinceUtc)}{amount}; then it is scanned for viruses, and the servers get the version as soon as the WinGet source of the central server offers it.";
        }

        if (step is { Kind: CacheStepKind.Scanning } scanning)
        {
            return $"The installer is being scanned for viruses since {Components.DisplayTime.Format(scanning.SinceUtc)}; the servers get the version as soon as the scan passed.";
        }

        var next = nextRunUtc is { } at ? $" at {Components.DisplayTime.Format(at)}" : string.Empty;
        if (version.ScanState == InstallerScanState.NotScanned && version.ScanDetail is { } failure && failure.StartsWith("Download failed", StringComparison.Ordinal))
        {
            return $"The last attempt to download the installer failed ({failure}); the installer cache tries again with its next run{next}.";
        }

        return version.ScanState switch
        {
            InstallerScanState.Detected => $"The virus scan found a threat in the installer ({version.ScanDetail ?? "no name given"}); the version is not offered and no server installs it, also not after a release to every ring. Block the version.",
            InstallerScanState.Failed => $"The virus scan of the installer failed ({version.ScanDetail ?? "no reason given"}); the version is not offered, so no server installs it, also not after a release to every ring. The central server tries again every few minutes and releases the version to the servers as soon as a scan succeeds.",
            _ => $"The installer waits for the next run of the installer cache{next}, which downloads it and scans it for viruses; the servers get the version as soon as the WinGet source of the central server offers it.",
        };
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
