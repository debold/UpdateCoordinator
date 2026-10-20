using System.Net;
using System.Net.NetworkInformation;

using SUSyphos.Contracts.Installation;
using SUSyphos.Contracts.Trust;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Trust;

namespace SUSyphos.Server.AgentPackages;

/// <summary>
/// Download of the agent installer from the dashboard. The file content is the unchanged MSI; its name carries the central
/// server address and the trust anchor fingerprint (docs/03-communication-protocol.md, section 3.3).
/// </summary>
internal static class AgentInstallerEndpoints
{
    public const string Route = "/administration/agent-installer";

    public static IEndpointRouteBuilder MapAgentInstallerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, DownloadAsync)
            .RequireAuthorization(AdminRoles.Policies.Operate)
            .WithName("AgentInstallerDownload");
        return endpoints;
    }

    internal static async Task<IResult> DownloadAsync(
        string? address,
        HttpContext httpContext,
        AgentPackageStore store,
        TrustAnchor anchor,
        AuditLog audit,
        CancellationToken cancellationToken)
    {
        if (!AgentInstallerFileName.IsValidCentralServerUrl(address) || !Uri.TryCreate(address, UriKind.Absolute, out var centralServer))
        {
            return TypedResults.BadRequest("The address must be an absolute https:// address of the central server.");
        }

        var package = await store.GetNewestAsync(cancellationToken).ConfigureAwait(false);
        var path = package is null ? null : await store.GetPathAsync(package.Version, cancellationToken).ConfigureAwait(false);
        if (package is null || path is null)
        {
            return TypedResults.NotFound("No agent package is available yet.");
        }

        // An address a file name cannot carry (IP v6, a path) still gives a working installer; the address is then entered at installation.
        var fileName = AgentInstallerFileName.Create(package.Version, centralServer, TrustBindingFormat.ComputeAnchorFingerprint(anchor.PublicKey))
            ?? AgentPackageStore.FileNameFor(AgentVersion.TryParse(package.Version, out var version) ? version : new AgentVersion(0, 0, 0, null));
        await audit.WriteAsync(httpContext.User.Identity?.Name ?? "unknown", "AgentPackage.InstallerDownloaded", package.Version, fileName, cancellationToken).ConfigureAwait(false);
        return TypedResults.PhysicalFile(path, "application/x-msi", fileName);
    }

    /// <summary>
    /// Address suggested for new agents: the one the dashboard is reached at, but always with a fully qualified name. The
    /// local machine (localhost, a loopback address or its short name) becomes the machine's FQDN; another short name is
    /// resolved through DNS. Agents in other domains or networks need the FQDN, so a short name is never suggested.
    /// </summary>
    public static async Task<string> SuggestAddressAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host.Host.Trim('[', ']');
        var port = request.Host.Port ?? 443;
        if (!IPAddress.TryParse(host, out var ip) || IPAddress.IsLoopback(ip))
        {
            host = await QualifyAsync(host, ip is not null, cancellationToken).ConfigureAwait(false);
        }

        return new UriBuilder(Uri.UriSchemeHttps, host.ToLowerInvariant(), port).Uri.ToString();
    }

    internal static async Task<string> QualifyAsync(string host, bool loopback, CancellationToken cancellationToken)
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var machine = string.IsNullOrEmpty(properties.DomainName) ? properties.HostName : $"{properties.HostName}.{properties.DomainName}";
        if (loopback || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(host, properties.HostName, StringComparison.OrdinalIgnoreCase))
        {
            return machine;
        }

        if (host.Contains('.', StringComparison.Ordinal))
        {
            return host;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var entry = await Dns.GetHostEntryAsync(host, timeout.Token).ConfigureAwait(false);
            return entry.HostName.Contains('.', StringComparison.Ordinal) ? entry.HostName : host;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
        {
            return host;
        }
    }
}
