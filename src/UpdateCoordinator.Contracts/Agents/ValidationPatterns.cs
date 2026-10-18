namespace UpdateCoordinator.Contracts.Agents;

/// <summary>
/// Regular expressions shared by the message contracts. Kept in one place so agent and server validate identically.
/// </summary>
public static class ValidationPatterns
{
    /// <summary>NetBIOS/DNS host label: letters, digits and hyphens, not starting or ending with a hyphen.</summary>
    public const string HostName = @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$";

    /// <summary>DNS domain name made of host labels separated by dots.</summary>
    public const string DomainName = @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$";

    /// <summary>Windows version with two to four numeric parts, e.g. 10.0.20348.2700.</summary>
    public const string OsVersion = @"^[0-9]{1,5}(?:\.[0-9]{1,5}){1,3}$";

    /// <summary>Semantic version 2.0 including optional pre-release and build metadata.</summary>
    public const string SemanticVersion = @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$";
}
