using SUSyphos.Contracts.Scripts;

namespace SUSyphos.Server.Data;

/// <summary>
/// A script-signing key (Marc, 2026-11-13): ECDSA P-256, certified by the trust anchor. The private key lives in a
/// DPAPI-protected file next to the anchor and goes into the backup like it; the table keeps the public key and the
/// certificate, so every revision stays verifiable after the key was rotated.
/// </summary>
internal sealed class ScriptKeyEntity
{
    public required string KeyId { get; set; }

    /// <summary>SubjectPublicKeyInfo (DER).</summary>
    public required byte[] PublicKey { get; set; }

    /// <summary>Signature of <see cref="ScriptSigningFormat.KeyCertificateBytes"/> by the trust anchor.</summary>
    public required byte[] Certificate { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When a newer key replaced it; null for the active key.</summary>
    public DateTime? RetiredAtUtc { get; set; }
}

/// <summary>The script of one phase of a WinGet package, with its draft; approved revisions are in <see cref="PackageScriptRevisionEntity"/>.</summary>
internal sealed class PackageScriptEntity
{
    public int Id { get; set; }

    public required string PackageId { get; set; }

    public ScriptPhase Phase { get; set; }

    /// <summary>The draft as text; null when the draft equals the approved revision. Drafts never reach an agent.</summary>
    public string? DraftContent { get; set; }

    public string? DraftParametersJson { get; set; }

    public ScriptRunOn DraftRunOn { get; set; }

    public ScriptOnFailure DraftOnFailure { get; set; }

    /// <summary>The PowerShell the draft runs in.</summary>
    public SUSyphos.Contracts.Scripts.ScriptPowerShell DraftPowerShell { get; set; }

    public int DraftTimeoutSeconds { get; set; } = ScriptLimits.DefaultTimeoutSeconds;

    public DateTime DraftChangedAtUtc { get; set; }

    public required string DraftChangedBy { get; set; }

    /// <summary>The revision agents run; null while none was approved.</summary>
    public int? ApprovedRevision { get; set; }
}

/// <summary>An approved, signed and immutable revision of a script.</summary>
internal sealed class PackageScriptRevisionEntity
{
    public int ScriptId { get; set; }

    public int Revision { get; set; }

    /// <summary>The signed bytes: UTF-8 with BOM and CRLF.</summary>
    public required byte[] Content { get; set; }

    public required string ContentSha256 { get; set; }

    public required string ParametersJson { get; set; }

    public required string ParametersSha256 { get; set; }

    public ScriptRunOn RunOn { get; set; }

    public ScriptOnFailure OnFailure { get; set; }

    /// <summary>Windows PowerShell 5.1 (default, all revisions before 0.9.0-rc.7) or PowerShell 7 with fallback to 5.1.</summary>
    public SUSyphos.Contracts.Scripts.ScriptPowerShell PowerShell { get; set; }

    public int TimeoutSeconds { get; set; }

    public required string KeyId { get; set; }

    public required byte[] Signature { get; set; }

    public DateTime SignedAtUtc { get; set; }

    public required string ApprovedBy { get; set; }

    public InstallerScanState ScanState { get; set; }

    public string? ScanDetail { get; set; }
}

/// <summary>The value of a script parameter for one assignment; secrets are stored DPAPI-protected only.</summary>
internal sealed class PackageAssignmentScriptValueEntity
{
    public int AssignmentId { get; set; }

    public int ScriptId { get; set; }

    public required string Name { get; set; }

    public string? Value { get; set; }

    public byte[]? ProtectedValue { get; set; }

    public DateTime ChangedAtUtc { get; set; }

    public required string ChangedBy { get; set; }
}

/// <summary>One script run on a server with its complete output (secrets masked), kept like the installation history.</summary>
internal sealed class ScriptRunEntity
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public required string PackageId { get; set; }

    public int? ScriptId { get; set; }

    public int? Revision { get; set; }

    public ScriptPhase Phase { get; set; }

    public ScriptAction Action { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime FinishedAtUtc { get; set; }

    public int? ExitCode { get; set; }

    public ScriptRunResult Result { get; set; }

    public string? Output { get; set; }

    public bool Truncated { get; set; }

    public int Lines { get; set; }

    public int Errors { get; set; }

    public int Warnings { get; set; }

    public string? Detail { get; set; }
}
