using System.Net;
using System.Net.Http.Json;

using SUSyphos.Agent.Identity;
using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Contracts.Scripts;

namespace SUSyphos.Agent.Scripts;

/// <summary>Downloads approved script revisions and reports script runs. Separate so the runner can be tested without HTTP.</summary>
internal interface IScriptClient
{
    /// <summary>The revision, or null when the central server does not hand it out (unknown, deleted, scripts switched off).</summary>
    Task<ScriptPackage?> GetScriptAsync(AgentIdentity identity, int scriptId, int revision, CancellationToken cancellationToken);

    /// <summary>The ZIP of files of a revision (0.9.0-rc.8), or null when the central server does not hand it out.</summary>
    Task<byte[]?> GetFilesAsync(AgentIdentity identity, int scriptId, int revision, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    Task<bool> SendRunAsync(AgentIdentity identity, ScriptRunReport report, CancellationToken cancellationToken);
}

/// <summary>Talks to the central server over the same validated TLS channel as the other agent calls.</summary>
internal sealed class ScriptClient(HttpClient httpClient) : IScriptClient
{
    public async Task<ScriptPackage?> GetScriptAsync(AgentIdentity identity, int scriptId, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.ScriptFor(scriptId, revision), UriKind.Relative));
        message.Headers.Authorization = AgentCredentials.ToHeader(identity.ClientId, identity.ClientSecret);
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ScriptLimits.MaxContentBytes * 2L + 64 * 1024)
        {
            throw new InvalidDataException("The script from the central server is too large.");
        }

        return await response.Content.ReadFromJsonAsync<ScriptPackage>(cancellationToken).ConfigureAwait(false);
    }


    public async Task<byte[]?> GetFilesAsync(AgentIdentity identity, int scriptId, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.ScriptFilesFor(scriptId, revision), UriKind.Relative));
        message.Headers.Authorization = AgentCredentials.ToHeader(identity.ClientId, identity.ClientSecret);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ScriptLimits.MaxFilesZipBytes)
        {
            throw new InvalidDataException("The files of the script from the central server are too large.");
        }

        // Read with a limit, also when the length is not announced.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > ScriptLimits.MaxFilesZipBytes)
            {
                throw new InvalidDataException("The files of the script from the central server are too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
    public async Task<bool> SendRunAsync(AgentIdentity identity, ScriptRunReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.AgentScriptRuns, UriKind.Relative)) { Content = JsonContent.Create(report) };
        message.Headers.Authorization = AgentCredentials.ToHeader(identity.ClientId, identity.ClientSecret);
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
}
