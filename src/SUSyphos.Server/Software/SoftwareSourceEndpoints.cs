using SUSyphos.WinGet.Catalog.RestSource;

namespace SUSyphos.Server.Software;

/// <summary>
/// The WinGet REST source of the central server (<see cref="SoftwareSourceCache.ApiPath"/>): the agents' WinGet reads
/// manifests and installers from here. Anonymous and read only, because WinGet sends no credentials to a REST source; it
/// offers only versions whose installers matched their SHA-256 and passed the virus scan, all of them public software.
/// </summary>
internal static partial class SoftwareSourceEndpoints
{
    public static IEndpointRouteBuilder MapSoftwareSourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(string.Empty).AllowAnonymous();

        // Switched off: the source answers nothing, as if it did not exist.
        group.AddEndpointFilter(async (context, next) =>
            await SoftwareFeature.IsEnabledAsync(context.HttpContext.RequestServices.GetRequiredService<Data.ServerDbContext>(), context.HttpContext.RequestAborted).ConfigureAwait(false)
                ? await next(context).ConfigureAwait(false)
                : Results.NotFound());

        // One line per installer download, so the log shows whether an agent's WinGet got as far as the download.
        group.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context).ConfigureAwait(false);
            var request = context.HttpContext.Request;
            if (request.Path.StartsWithSegments(SoftwareSourceCache.FilesPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SoftwareSourceEndpoints));
                var address = context.HttpContext.Connection.RemoteIpAddress is { } remote ? remote.ToString() : "unknown";
                LogInstallerRequested(logger, request.Path.Value ?? string.Empty, address, request.Headers.Range.Count > 0);
            }

            return result;
        });
        var cache = endpoints.ServiceProvider.GetRequiredService<SoftwareSourceCache>();
        RestSourceEndpoints.Map(group, SoftwareSourceCache.ApiPath, SoftwareSourceCache.FilesPath, cache.Responder, () => cache.FilesFolder);

        // Logos for the dashboard: only names the logo cache gives, only for signed-in readers.
        endpoints.MapGet(SoftwareLogos.Route + "{name}", (string name, SoftwareLogos logos) =>
            {
                if (!SoftwareLogos.IsLogoFileName(name))
                {
                    return Results.NotFound();
                }

                var path = Path.Combine(logos.Folder, name);
                return File.Exists(path) ? Results.File(path, name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/x-icon") : Results.NotFound();
            })
            .RequireAuthorization(Administration.AdminRoles.Policies.Read);
        return endpoints;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WinGet source: installer {Path} requested by {Address} (range request: {Range}).")]
    private static partial void LogInstallerRequested(ILogger logger, string path, string address, bool range);
}
