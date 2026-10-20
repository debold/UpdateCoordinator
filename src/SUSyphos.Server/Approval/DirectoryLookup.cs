using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;

namespace SUSyphos.Server.Approval;

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

    /// <summary>Enabled computer accounts with a Windows Server operating system; throws if the directory cannot be reached.</summary>
    Task<IReadOnlyList<DirectoryComputer>> ListServersAsync(string domain, CancellationToken cancellationToken);
}

/// <summary>A server computer account from Active Directory.</summary>
/// <param name="Name">Account name without the trailing dollar sign.</param>
/// <param name="DnsHostName">Full DNS name, if set.</param>
/// <param name="OperatingSystem">E.g. "Windows Server 2019 Standard".</param>
/// <param name="OperatingSystemVersion">E.g. "10.0 (17763)".</param>
internal sealed record DirectoryComputer(string Name, string? DnsHostName, string? OperatingSystem, string? OperatingSystemVersion);

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

    public Task<IReadOnlyList<DirectoryComputer>> ListServersAsync(string domain, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DirectoryComputer>>(() => ListServers(domain, cancellationToken), cancellationToken);

    /// <summary>Signed and sealed connection with the service account, and the default naming context of the domain.</summary>
    private static (LdapConnection Connection, string BaseDn) Connect(string domain)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(domain))
        {
            AuthType = AuthType.Negotiate,
            Timeout = Timeout,
        };
        try
        {
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.Signing = true;
            connection.SessionOptions.Sealing = true;
            connection.Bind();

            var rootDse = (SearchResponse)connection.SendRequest(new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));
            return (connection, (string)rootDse.Entries[0].Attributes["defaultNamingContext"][0]);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static List<DirectoryComputer> ListServers(string domain, CancellationToken cancellationToken)
    {
        var (connection, baseDn) = Connect(domain);
        using (connection)
        {
            // Enabled computer accounts (bit 2 of userAccountControl not set) with a server operating system; paged for large domains.
            const string Filter = "(&(objectCategory=computer)(operatingSystem=Windows Server*)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";
            var request = new SearchRequest(baseDn, Filter, SearchScope.Subtree, "sAMAccountName", "dNSHostName", "operatingSystem", "operatingSystemVersion");
            var paging = new PageResultRequestControl(500);
            request.Controls.Add(paging);

            var computers = new List<DirectoryComputer>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = (SearchResponse)connection.SendRequest(request);
                foreach (SearchResultEntry entry in response.Entries)
                {
                    var name = Text(entry, "sAMAccountName")?.TrimEnd('$');
                    if (!string.IsNullOrEmpty(name))
                    {
                        computers.Add(new DirectoryComputer(name, Text(entry, "dNSHostName"), Text(entry, "operatingSystem"), Text(entry, "operatingSystemVersion")));
                    }
                }

                var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
                if (cookie is not { Length: > 0 } || computers.Count >= MaxComputers)
                {
                    return computers;
                }

                paging.Cookie = cookie;
            }
        }
    }

    /// <summary>Upper bound of accounts read, so a huge directory cannot exhaust memory.</summary>
    private const int MaxComputers = 100_000;

    private static string? Text(SearchResultEntry entry, string attribute)
    {
        var values = entry.Attributes[attribute];
        var value = values is { Count: > 0 } ? values[0] as string : null;
        return value is { Length: > 0 and <= 512 } ? value : null;
    }

    private static ComputerAccountState Find(string domain, string hostName)
    {
        var (connection, baseDn) = Connect(domain);
        using var owned = connection;

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
