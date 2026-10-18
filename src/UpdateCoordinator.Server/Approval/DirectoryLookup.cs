using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;

namespace UpdateCoordinator.Server.Approval;

/// <summary>
/// State of a computer account in Active Directory.
/// </summary>
internal enum ComputerAccountState
{
    NotFound,
    Enabled,
    Disabled,
}

/// <summary>
/// Queries Active Directory for computer accounts. The central server must be a domain member;
/// it queries with its own service account.
/// </summary>
internal interface IDirectoryLookup
{
    /// <summary>Throws if the directory cannot be reached, so the caller can retry later.</summary>
    Task<ComputerAccountState> FindComputerAsync(string domain, string hostName, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves host names to IP addresses.
/// </summary>
internal interface IDnsLookup
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostName, CancellationToken cancellationToken);
}

internal sealed class SystemDnsLookup : IDnsLookup
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostName, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(hostName, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// LDAP lookup through System.DirectoryServices.Protocols with the credentials of the service account (Kerberos/NTLM).
/// </summary>
internal sealed class LdapDirectoryLookup : IDirectoryLookup
{
    private const int AccountDisabled = 0x2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public Task<ComputerAccountState> FindComputerAsync(string domain, string hostName, CancellationToken cancellationToken) =>
        Task.Run(() => Find(domain, hostName), cancellationToken);

    private static ComputerAccountState Find(string domain, string hostName)
    {
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(domain))
        {
            AuthType = AuthType.Negotiate,
            Timeout = Timeout,
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.Signing = true;
        connection.SessionOptions.Sealing = true;
        connection.Bind();

        var rootDse = (SearchResponse)connection.SendRequest(new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));
        var baseDn = (string)rootDse.Entries[0].Attributes["defaultNamingContext"][0];

        var filter = $"(&(objectCategory=computer)(sAMAccountName={EscapeFilterValue(hostName + "$")}))";
        var response = (SearchResponse)connection.SendRequest(new SearchRequest(baseDn, filter, SearchScope.Subtree, "userAccountControl") { SizeLimit = 2 });
        if (response.Entries.Count != 1)
        {
            return ComputerAccountState.NotFound;
        }

        var control = response.Entries[0].Attributes["userAccountControl"];
        return control is not null && int.TryParse((string)control[0], out var flags) && (flags & AccountDisabled) != 0
            ? ComputerAccountState.Disabled
            : ComputerAccountState.Enabled;
    }

    /// <summary>Escapes a value for an LDAP search filter (RFC 4515), so input can never change the filter.</summary>
    internal static string EscapeFilterValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '*' or '(' or ')' or '\0' || c > 0x7E || c < 0x20)
            {
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                {
                    builder.Append('\\').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
