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
        ArgumentNullException.ThrowIfNull(identity);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentHeartbeat, UriKind.Relative))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = AgentCredentials.ToHeader(identity.ClientId, identity.ClientSecret);

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(cancellationToken).ConfigureAwait(false);
        if (body is null || !Enum.IsDefined(body.State))
        {
            throw new InvalidDataException("The central server returned an invalid heartbeat response.");
        }

        return body;
    }
}
