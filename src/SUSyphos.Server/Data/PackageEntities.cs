namespace SUSyphos.Server.Data;

/// <summary>What an entry of the update catalog is: a Windows update or a software version installed with WinGet.</summary>
internal enum UpdateKind
{
    WindowsUpdate = 0,
    Software = 1,
}

/// <summary>
/// A WinGet package in the catalog of the central server (docs/04-central-server.md, software): matched from the
/// software inventory of the servers or added by an administrator. Its versions are entries of the update catalog
/// (update kind software), so rollouts, holds and reports work as for Windows updates.
/// </summary>
internal sealed class PackageEntity
{
    public const int MaxIdLength = 128;
    public const int MaxNameLength = 256;
    public const int MaxUrlLength = 2048;

    /// <summary>WinGet package identifier, e.g. "7zip.7zip"; compared without case like WinGet does.</summary>
    public required string PackageId { get; set; }

    public required string Name { get; set; }

    public string? Publisher { get; set; }

    public string? ShortDescription { get; set; }

    public string? Description { get; set; }

    public string? Moniker { get; set; }

    /// <summary>Tags of the manifest, comma-separated.</summary>
    public string? Tags { get; set; }

    public string? License { get; set; }

    public string? LicenseUrl { get; set; }

    public string? PackageUrl { get; set; }

    public string? PublisherUrl { get; set; }

    public string? ReleaseNotesUrl { get; set; }

    /// <summary>File name of the cached logo (SHA-256 of its content plus extension); null without a logo.</summary>
    public string? LogoFile { get; set; }

    /// <summary>The manifest carries agreements (licence terms) the installation accepts; such packages never start automatically.</summary>
    public bool HasAgreements { get; set; }

    /// <summary>When an administrator accepted the agreements for this package; null while not accepted.</summary>
    public DateTime? AgreementsAcceptedAtUtc { get; set; }

    public string? AgreementsAcceptedBy { get; set; }

    public DateTime AddedAtUtc { get; set; }

    /// <summary>Account that added the package, or "inventory" when it was matched from the servers' software.</summary>
    public required string AddedBy { get; set; }

    /// <summary>Last time the central server looked for new versions; null if never.</summary>
    public DateTime? CheckedAtUtc { get; set; }
}

/// <summary>Result of the virus scan of a cached installer (AMSI).</summary>
internal enum InstallerScanState
{
    NotScanned = 0,
    Clean = 1,
    Detected = 2,
    Failed = 3,
}

/// <summary>A known version of a package, with the state of its cached installer.</summary>
internal sealed class PackageVersionEntity
{
    public const int MaxVersionLength = 64;

    public required string PackageId { get; set; }

    public required string Version { get; set; }

    /// <summary>The catalog entry of this version (revision 1), see <see cref="Software.SoftwarePackages.SoftwareUpdateId"/>.</summary>
    public Guid UpdateId { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    /// <summary>Manifest of the version as JSON, served by the WinGet REST source of the central server.</summary>
    public string? Manifest { get; set; }

    /// <summary>File name of the cached installer; null while it is not cached.</summary>
    public string? InstallerFile { get; set; }

    /// <summary>SHA-256 of the installer from the manifest, as 64 hex digits; the cache checks every download against it.</summary>
    public string? InstallerSha256 { get; set; }

    public long? SizeBytes { get; set; }

    public DateTime? CachedAtUtc { get; set; }

    public InstallerScanState ScanState { get; set; }

    /// <summary>What the scanner reported (threat name or error); null when clean or not scanned.</summary>
    public string? ScanDetail { get; set; }

    public DateTime? ScannedAtUtc { get; set; }
}

/// <summary>Where an entry of the software inventory comes from (registry view of the uninstall keys).</summary>
internal enum SoftwareView
{
    Machine64 = 0,
    Machine32 = 1,
    Msix = 2,
}

/// <summary>How an installed program was matched to a WinGet package.</summary>
internal enum SoftwareMatch
{
    /// <summary>No package is known for it.</summary>
    None = 0,

    /// <summary>WinGet itself correlated the installation with the package.</summary>
    WinGet = 1,

    /// <summary>Matched by the central server (product code or name and publisher).</summary>
    Catalog = 2,
}

/// <summary>A program installed on a server, as last reported by its software inventory.</summary>
internal sealed class ClientSoftwareEntity
{
    public const int MaxKeyLength = 256;
    public const int MaxTextLength = 256;

    public Guid ClientId { get; set; }

    public SoftwareView View { get; set; }

    /// <summary>Name of the uninstall key, or the package family name for MSIX.</summary>
    public required string KeyName { get; set; }

    public required string DisplayName { get; set; }

    public string? DisplayVersion { get; set; }

    public string? Publisher { get; set; }

    /// <summary>The WinGet package it belongs to; null if unknown.</summary>
    public string? PackageId { get; set; }

    /// <summary>The installed version in WinGet's notation, which may differ from <see cref="DisplayVersion"/>.</summary>
    public string? InstalledVersion { get; set; }

    public SoftwareMatch MatchKind { get; set; }

    public DateTime ReportedAtUtc { get; set; }
}

internal enum PackageAssignmentTarget
{
    Group = 0,
    Server = 1,
}

internal enum PackageAssignmentMode
{
    Install = 0,
    Uninstall = 1,
}

/// <summary>
/// Assignment of a package to a group or a server (new deployments): the package is installed when its version is
/// released for the server's ring. Removing the assignment uninstalls only with <see cref="UninstallOnRemoval"/>.
/// </summary>
internal sealed class PackageAssignmentEntity
{
    public int Id { get; set; }

    public required string PackageId { get; set; }

    public PackageAssignmentTarget TargetKind { get; set; }

    public int? GroupId { get; set; }

    public Guid? ClientId { get; set; }

    public PackageAssignmentMode Mode { get; set; }

    /// <summary>A fixed version to install; null installs the newest version released for the ring.</summary>
    public string? FixedVersion { get; set; }

    public bool UninstallOnRemoval { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public required string CreatedBy { get; set; }
}
