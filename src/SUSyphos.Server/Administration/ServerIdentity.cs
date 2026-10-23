using System.Net.NetworkInformation;

namespace SUSyphos.Server.Administration;

/// <summary>The name and address of this central server as other machines reach it by default.</summary>
internal static class ServerIdentity
{
    /// <summary>Fully qualified name of this machine, e.g. "uc01.lab.local" (the host name without a domain on a workgroup machine).</summary>
    public static string MachineFqdn
    {
        get
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            return (string.IsNullOrEmpty(properties.DomainName) ? properties.HostName : $"{properties.HostName}.{properties.DomainName}").ToLowerInvariant();
        }
    }

    /// <summary>
    /// The dashboard address built from the machine name and the HTTPS port the server listens on (Kestrel:Endpoints:Https:Url,
    /// e.g. "https://*:8443"), such as "https://uc01.lab.local:8443/".
    /// </summary>
    public static string DefaultAddress(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var url = configuration["Kestrel:Endpoints:Https:Url"];
        var port = 443;
        if (url is not null && url.LastIndexOf(':') is var colon and > 0 && int.TryParse(url[(colon + 1)..].TrimEnd('/'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 65535)
        {
            port = parsed;
        }

        return new UriBuilder(Uri.UriSchemeHttps, MachineFqdn, port).Uri.ToString();
    }
}
