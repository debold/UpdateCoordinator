using System.Net;
using System.Net.Security;
using System.Security;
using System.Text.Json;

using Microsoft.Extensions.Options;
using Microsoft.Win32;

using SUSyphos.Agent.Actions;
using SUSyphos.Agent.Configuration;
using SUSyphos.Agent.Identity;
using SUSyphos.Contracts;
using SUSyphos.Contracts.Agents;
using SUSyphos.Contracts.Trust;

namespace SUSyphos.Agent.Trust;

/// <summary>Where the agent keeps the central server address (registry value CentralServerUrl).</summary>
internal interface IAgentAddressStore
{
    /// <summary>True if group policy sets the address; the agent then never changes it.</summary>
    bool IsSetByPolicy { get; }

    void Save(Uri address);
}

/// <summary>Writes the installer value; the policy value always wins and is never touched.</summary>
internal sealed class RegistryAgentAddressStore : IAgentAddressStore
{
    private const string ValueName = "CentralServerUrl";

    public bool IsSetByPolicy
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(AgentRegistry.PolicyKeyPath);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    public void Save(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        using var key = Registry.LocalMachine.CreateSubKey(AgentRegistry.InstallerKeyPath, writable: true);
        key.SetValue(ValueName, address.AbsoluteUri, RegistryValueKind.String);
    }
}

/// <summary>Asks the move target with the agent's credentials whether it knows the agent, over a connection pinned to the verified certificate.</summary>
internal interface IMoveTargetProbe
{
    Task<HttpStatusCode> ProbeAsync(Uri address, string acceptedCertificateSpkiSha256, AgentIdentity identity, CancellationToken cancellationToken);
}

internal sealed class MoveTargetProbe : IMoveTargetProbe
{
    public async Task<HttpStatusCode> ProbeAsync(Uri address, string acceptedCertificateSpkiSha256, AgentIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var accepted = Convert.FromBase64String(acceptedCertificateSpkiSha256);
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // Credentials are only sent to the certificate the trust anchor vouched for.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }

                    using var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
                    return TrustBindingFormat.ComputeSpkiHash(x509).AsSpan().SequenceEqual(accepted);
                },
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.AgentReleases, UriKind.Relative));
        message.Headers.Authorization = AgentCredentials.ToHeader(identity.ClientId, identity.ClientSecret);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }
}

/// <summary>
/// Moves the agent to a new central server address the current server announces (docs/03-communication-protocol.md, 3.4).
/// The agent switches only if the server at the new address proves the pinned trust anchor and accepts the agent's credentials,
/// so a move can never hand the agent to a foreign server. Otherwise it stays and reports why.
/// </summary>
internal sealed partial class ServerMover(
    IServerTrust trust,
    ITrustBindingFetcher fetcher,
    IMoveTargetProbe probe,
    IAgentAddressStore addresses,
    IServiceRestarter restarter,
    IOptions<CentralServerOptions> options,
    TimeProvider timeProvider,
    ILogger<ServerMover> logger)
{
    /// <summary>A failed move is tried again after this time, not with every heartbeat.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    private const int MaxErrorLength = 400;

    private Uri? _lastTarget;
    private DateTimeOffset _lastAttempt;

    /// <summary>Why the announced move did not happen, for the next heartbeat; null if none is announced or it has not been tried yet.</summary>
    public string? LastError { get; private set; }

    /// <summary>Tries an announced move; true if the agent moved and its service restarts.</summary>
    public async Task<bool> MoveIfAnnouncedAsync(AgentIdentity identity, HeartbeatResponse heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        if (heartbeat.State != RegistrationState.Approved || heartbeat.MoveTo is null)
        {
            LastError = null;
            _lastTarget = null;
            return false;
        }

        if (!CentralServerAddress.TryNormalize(heartbeat.MoveTo.OriginalString, out var address) || address is null)
        {
            LastError = "The announced address is not a valid HTTPS address.";
            return false;
        }

        if (CentralServerAddress.AreSame(address, options.Value.BaseAddress))
        {
            // The new server itself, restored with the announcement still set.
            LastError = null;
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (address == _lastTarget && now - _lastAttempt < RetryInterval)
        {
            return false;
        }

        _lastTarget = address;
        _lastAttempt = now;
        var error = await TryMoveAsync(address, identity, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            LastError = Shorten(error);
            LogMoveFailed(address, LastError);
            return false;
        }

        LastError = null;
        LogMoved(address);
        restarter.RequestRestart();
        return true;
    }

    private async Task<string?> TryMoveAsync(Uri address, AgentIdentity identity, CancellationToken cancellationToken)
    {
        if (addresses.IsSetByPolicy)
        {
            return @"The central server address is set by group policy (CentralServerUrl under HKLM\SOFTWARE\Policies\SUSyphos\Agent); change it there.";
        }

        TrustBinding? binding;
        byte[] presented;
        try
        {
            (binding, presented) = await fetcher.FetchFromAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionError(ex, cancellationToken))
        {
            return $"{address} is not reachable: {ex.Message}";
        }

        var decision = trust.CheckMoveTarget(binding, presented, out var newState);
        if (decision != TrustDecision.Accepted)
        {
            return decision switch
            {
                TrustDecision.AnchorMismatch => $"The central server at {address} has another trust anchor; restore it from a backup of this central server.",
                TrustDecision.OutdatedSerial => $"The central server at {address} presents an older certificate than this agent knows; restore it from a newer backup.",
                _ => $"The central server at {address} cannot prove its identity ({decision}).",
            };
        }

        HttpStatusCode status;
        try
        {
            status = await probe.ProbeAsync(address, newState.AcceptedCertificateSpkiSha256!, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionError(ex, cancellationToken))
        {
            return $"{address} is not reachable: {ex.Message}";
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            return $"The central server at {address} does not know this agent; restore it from a current backup of this central server.";
        }

        if ((int)status is < 200 or > 299)
        {
            return $"The central server at {address} answered with HTTP status {(int)status}.";
        }

        try
        {
            addresses.Save(address);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"The new address could not be saved: {ex.Message}";
        }

        trust.AdoptMoveTarget(newState);
        return null;
    }

    private static bool IsConnectionError(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or JsonException or NotSupportedException or InvalidOperationException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>Keeps the error within what the heartbeat accepts: printable and short.</summary>
    internal static string Shorten(string error)
    {
        var printable = new string(error.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return printable.Length <= MaxErrorLength ? printable : printable[..(MaxErrorLength - 1)] + "…";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not moved to the announced central server {Address}: {Error}")]
    private partial void LogMoveFailed(Uri address, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Moved to the central server {Address}; it proved the same trust anchor. The agent service restarts to use it.")]
    private partial void LogMoved(Uri address);
}
