using System.Net;

using SUSyphos.Agent.Identity;
using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.SelfUpdate;

/// <summary>
/// Downloads agent packages from the central server. A client of its own: the package is streamed to disk instead of held
/// in memory, and the download may take longer than the timeouts of the regular messages allow.
/// </summary>
internal sealed class AgentPackageClient(HttpClient httpClient) : IAgentPackageClient
{
    public async Task<bool> DownloadAsync(AgentIdentity identity, string version, Stream target, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(target);
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentPackageFor(version), UriKind.Relative));
        message.Headers.Authorization = AgentCredentials.ToHeader(identity.ClientId, identity.ClientSecret);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException("The central server sent a larger package than announced.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException("The central server sent a larger package than announced.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
