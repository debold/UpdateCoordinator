using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>
/// The WinGet REST source of the central server (<see cref="SoftwareSourceCache.ApiPath"/>): the agents' WinGet reads
/// manifests and installers from here. Anonymous and read only, because WinGet sends no credentials to a REST source; it
/// offers only versions whose installers matched their SHA-256 and passed the virus scan, all of them public software.
/// </summary>
internal static class SoftwareSourceEndpoints
{
    public static IEndpointRouteBuilder MapSoftwareSourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(string.Empty).AllowAnonymous();
        var cache = endpoints.ServiceProvider.GetRequiredService<SoftwareSourceCache>();
        RestSourceEndpoints.Map(group, SoftwareSourceCache.ApiPath, SoftwareSourceCache.FilesPath, cache.Responder, () => cache.FilesFolder);
        return endpoints;
    }
}
