using System.Security.Principal;

namespace UpdateCoordinator.Agent.Inventory;

/// <summary>
/// Looks up the SID of this server's computer account in Active Directory.
/// </summary>
internal interface IComputerSidReader
{
    /// <summary>Returns the SID, or null for workgroup servers or when no domain controller answers.</summary>
    string? GetComputerSid(string? domainName);
}

internal sealed partial class ComputerSidReader(ILogger<ComputerSidReader> logger) : IComputerSidReader
{
    public string? GetComputerSid(string? domainName)
    {
        if (domainName is null)
        {
            return null;
        }

        try
        {
            // "HOST$" resolves to the computer account of this machine in its domain.
            var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName + "$").Translate(typeof(SecurityIdentifier));
            return sid.IsAccountSid() ? sid.Value : null;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            LogSidUnavailable(ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Computer account SID cannot be determined ({Reason}).")]
    private partial void LogSidUnavailable(string reason);
}
