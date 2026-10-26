namespace SUSyphos.Server.Data;

/// <summary>
/// The security baseline of one Windows Server version in one monthly MSRC release (Security Update Guide, CVRF API):
/// the highest build that fixes the vulnerabilities published that month, and the KB that brings it.
/// </summary>
internal sealed class SecurityBaselineEntity
{
    /// <summary>Build of the Windows version, e.g. 20348 for Windows Server 2022.</summary>
    public int Build { get; set; }

    /// <summary>MSRC document, e.g. "2026-Oct".</summary>
    public required string Month { get; set; }

    /// <summary>When Microsoft first released the document (patch day).</summary>
    public DateTime ReleasedAtUtc { get; set; }

    /// <summary>Update revision of the target build, e.g. 5761 for 10.0.20348.5761.</summary>
    public int Revision { get; set; }

    /// <summary>KB number of the update that brings the target build, digits only.</summary>
    public string? Kb { get; set; }
}

/// <summary>One vulnerability (CVE) of the MSRC releases kept by the central server.</summary>
internal sealed class SecurityVulnerabilityEntity
{
    public required string Cve { get; set; }

    public required string Month { get; set; }

    public required string Title { get; set; }

    /// <summary>Critical, Important, Moderate or Low; null when Microsoft gives none for Windows Server.</summary>
    public string? Severity { get; set; }

    /// <summary>E.g. "Remote Code Execution", "Elevation of Privilege".</summary>
    public string? Impact { get; set; }

    /// <summary>Highest CVSS base score for Windows Server.</summary>
    public double? CvssScore { get; set; }

    /// <summary>Microsoft knows of attacks that use it ("Exploited: Yes").</summary>
    public bool Exploited { get; set; }

    public bool PubliclyDisclosed { get; set; }
}

/// <summary>Which build of a Windows Server version fixes a vulnerability, and with which KB.</summary>
internal sealed class SecurityFixEntity
{
    public required string Cve { get; set; }

    /// <summary>MSRC document the fix was read from; each monthly document is replaced as a whole.</summary>
    public required string Month { get; set; }

    public int Build { get; set; }

    /// <summary>Update revision that fixes it; a server on this revision or later is not affected.</summary>
    public int Revision { get; set; }

    public string? Kb { get; set; }
}
