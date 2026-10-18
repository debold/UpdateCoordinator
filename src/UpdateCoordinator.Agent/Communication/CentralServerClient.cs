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
            || body.Releases.Any(r => r is null || r.UpdateId == Guid.Empty || r.Revision < 0))
        {
            throw new InvalidDataException("The central server returned an invalid release list.");
        }

        return body;
    }

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
