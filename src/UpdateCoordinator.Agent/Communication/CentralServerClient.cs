using System.Net;
using System.Net.Http.Json;

using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Communication;

/// <summary>
/// HTTP client for the agent API of the central server. Resilience (timeouts, circuit breaker)
/// is configured on the underlying HttpClient, see <see cref="AgentHostBuilder"/>.
/// </summary>
internal sealed class CentralServerClient(HttpClient httpClient) : ICentralServerClient
{
    /// <summary>Upper bound for a client secret accepted from the server.</summary>
    private const int MaxSecretLength = 64;

    public async Task<AgentIdentity> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .PostAsJsonAsync(new Uri(ApiRoutes.AgentRegister, UriKind.Relative), request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken).ConfigureAwait(false);

        // Responses are input too: never trust them without validation.
        if (body is null
            || body.ClientId == Guid.Empty
            || string.IsNullOrEmpty(body.ClientSecret)
            || body.ClientSecret.Length > MaxSecretLength)
        {
            throw new InvalidDataException("The central server returned an invalid registration response.");
        }

        return new AgentIdentity(body.ClientId, body.ClientSecret);
    }

    public async Task<HeartbeatResponse?> HeartbeatAsync(AgentIdentity identity, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        using var response = await PostAuthenticatedAsync(ApiRoutes.AgentHeartbeat, identity, request, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(cancellationToken).ConfigureAwait(false);
        if (body is null || !Enum.IsDefined(body.State))
        {
            throw new InvalidDataException("The central server returned an invalid heartbeat response.");
        }

        return body;
    }

    public async Task<bool> SendInventoryAsync(AgentIdentity identity, InventoryRequest request, CancellationToken cancellationToken)
    {
        using var response = await PostAuthenticatedAsync(ApiRoutes.AgentInventory, identity, request, cancellationToken).ConfigureAwait(false);
        return response is not null;
    }

    public async Task<bool> SendUpdateReportAsync(AgentIdentity identity, UpdateReport report, CancellationToken cancellationToken)
    {
        using var response = await PostAuthenticatedAsync(ApiRoutes.AgentUpdateReport, identity, report, cancellationToken).ConfigureAwait(false);
        return response is not null;
    }

    public async Task<ReleaseList?> GetReleasesAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentReleases, UriKind.Relative));
        using var response = await SendAuthenticatedAsync(message, identity, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<ReleaseList>(cancellationToken).ConfigureAwait(false);
        if (body is null
            || body.Version < 0
            || body.Releases is null
            || body.Releases.Count > MaxReleases
            || body.Releases.Any(r => r is null || r.UpdateId == Guid.Empty || r.Revision < 0)
            || (body.MaintenanceWindows is { } windows && (windows.Count > MaintenanceWindow.MaxWindows || windows.Any(w => !IsValid(w))))
            || (body.RebootPolicy is { } policy && !Enum.IsDefined(policy)))
        {
            throw new InvalidDataException("The central server returned an invalid release list.");
        }

        return body;
    }

    public async Task<bool> SendInstallReportAsync(AgentIdentity identity, InstallReport report, CancellationToken cancellationToken)
    {
        using var response = await PostAuthenticatedAsync(ApiRoutes.AgentInstallReport, identity, report, cancellationToken).ConfigureAwait(false);
        return response is not null;
    }

    public async Task<AgentActionList?> GetActionsAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentActions, UriKind.Relative));
        using var response = await SendAuthenticatedAsync(message, identity, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<AgentActionList>(cancellationToken).ConfigureAwait(false);
        if (body?.Actions is null
            || body.Actions.Count > AgentActionList.MaxActions
            || body.Actions.Any(a => a is null || a.Id == Guid.Empty))
        {
            throw new InvalidDataException("The central server returned an invalid action list.");
        }

        return body;
    }

    public async Task<bool> SendActionResultAsync(AgentIdentity identity, Guid actionId, AgentActionResult result, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAuthenticatedAsync(ApiRoutes.AgentActionResultFor(actionId), identity, result, cancellationToken).ConfigureAwait(false);
            return response is not null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The action expired or was not ours; nothing left to report.
            return true;
        }
    }

    public async Task<bool> UploadLogsAsync(AgentIdentity identity, Guid actionId, byte[] archive, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(archive);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentActionLogsFor(actionId), UriKind.Relative)) { Content = content };
        using var response = await SendAuthenticatedAsync(message, identity, cancellationToken).ConfigureAwait(false);
        return response is not null;
    }

    private static bool IsValid(MaintenanceWindow? window) =>
        window is { Days: not null }
        && System.ComponentModel.DataAnnotations.Validator.TryValidateObject(window, new System.ComponentModel.DataAnnotations.ValidationContext(window), null, validateAllProperties: true);

    /// <summary>Upper bound for releases accepted from the server.</summary>
    private const int MaxReleases = 5_000;

    /// <summary>Posts a message with client credentials. Returns null for 401 and throws for every other error status.</summary>
    private async Task<HttpResponseMessage?> PostAuthenticatedAsync<T>(string route, AgentIdentity identity, T body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(route, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        return await SendAuthenticatedAsync(message, identity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a request with client credentials. Returns null for 401 and throws for every other error status.</summary>
    private async Task<HttpResponseMessage?> SendAuthenticatedAsync(HttpRequestMessage message, AgentIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        message.Headers.Authorization = AgentCredentials.ToHeader(identity.ClientId, identity.ClientSecret);

        var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            return null;
        }

        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
