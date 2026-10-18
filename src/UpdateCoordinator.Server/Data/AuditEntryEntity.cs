namespace UpdateCoordinator.Server.Data;

/// <summary>
/// One entry of the audit log (docs/04-central-server.md, section 6). Append-only: the database rejects
/// updates and deletes, and every entry carries a hash over its content and the previous hash, so any
/// later modification of the file breaks the chain.
/// </summary>
internal sealed class AuditEntryEntity
{
    public long Id { get; set; }

    public DateTime AtUtc { get; set; }

    /// <summary>User name, "system" or "agent:&lt;client id&gt;".</summary>
    public required string Actor { get; set; }

    /// <summary>Action identifier, e.g. "Login.Succeeded".</summary>
    public required string Action { get; set; }

    public string? Target { get; set; }

    public string? Details { get; set; }

    public required byte[] PreviousHash { get; set; }

    public required byte[] Hash { get; set; }
}
