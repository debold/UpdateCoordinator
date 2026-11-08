using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SUSyphos.WinGet.Catalog.RestSource;

/// <summary>
/// The routes of a private WinGet REST source (protocol 1.1): information, search, manifests and the cached installer
/// files. Shared by the central server and the lab tool, so both answer exactly alike.
/// </summary>
public static class RestSourceEndpoints
{
    /// <summary>A search request is a small JSON document; anything larger is refused.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>Maps the routes below <paramref name="apiPath"/> and <paramref name="filesPath"/> (both ending in "/").</summary>
    /// <param name="routes">Where to map.</param>
    /// <param name="apiPath">Path of the source, e.g. "/winget/api/".</param>
    /// <param name="filesPath">Path of the installer files, e.g. "/winget/files/".</param>
    /// <param name="responder">The responder for the current cache and the address of the request; asked per request, so a refreshed cache shows at once.</param>
    /// <param name="filesFolder">Folder of the installer files of the current cache.</param>
    /// <param name="log">Receives one line per request; may be null.</param>
    public static void Map(IEndpointRouteBuilder routes, string apiPath, string filesPath, Func<HttpRequest, RestSourceResponder> responder, Func<string> filesFolder, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentNullException.ThrowIfNull(filesFolder);
        routes.MapGet(apiPath + "information", (HttpRequest request) => Answer(responder(request).Information()));
        routes.MapPost(apiPath + "manifestSearch", async (HttpRequest request) =>
        {
            if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = MaxRequestBytes;
            }

            JsonObject? body;
            try
            {
                body = await JsonNode.ParseAsync(request.Body) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest();
            }
            catch (BadHttpRequestException exception)
            {
                return Results.StatusCode(exception.StatusCode);
            }

            RestAnswer answer;
            try
            {
                answer = responder(request).Search(body);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                // A field of the wrong JSON type (e.g. a number where text belongs).
                return Results.BadRequest();
            }

            log?.Invoke($"Search {body?.ToJsonString()} -> {answer.Status}");
            return Answer(answer);
        });
        routes.MapGet(apiPath + "packageManifests/{id}", (string id, string? version, HttpRequest request) =>
        {
            var answer = responder(request).Manifest(id, version);
            log?.Invoke($"Manifest {id} {version} -> {answer.Status}");
            return Answer(answer);
        });
        routes.MapGet(filesPath + "{name}", (string name) =>
        {
            // Only names the cache produces (SHA-256 plus extension) are served, so no path leaves the folder.
            if (!PackageCache.IsCacheFileName(name))
            {
                return Results.NotFound();
            }

            var path = Path.Combine(filesFolder(), name);
            log?.Invoke($"File {name}");
            return File.Exists(path) ? Results.File(path, "application/octet-stream", enableRangeProcessing: true) : Results.NotFound();
        });
    }

    private static IResult Answer(RestAnswer answer) =>
        answer.Body is null ? Results.StatusCode(answer.Status) : Results.Content(answer.Body.ToJsonString(), "application/json", statusCode: answer.Status);
}
