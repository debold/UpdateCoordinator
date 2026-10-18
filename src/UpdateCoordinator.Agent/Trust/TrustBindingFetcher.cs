using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Options;

using UpdateCoordinator.Contracts;
using UpdateCoordinator.Contracts.Trust;

namespace UpdateCoordinator.Agent.Trust;

/// <summary>
/// Fetches the trust binding together with the key hash of the certificate the server presented.
/// </summary>
internal interface ITrustBindingFetcher
{
    Task<(TrustBinding? Binding, byte[] PresentedCertificateSpkiSha256)> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Uses a dedicated connection that accepts any certificate, because the binding is what establishes trust.
/// Nothing but this anonymous GET is ever sent over it, and the response is verified before use.
/// </summary>
internal sealed class TrustBindingFetcher(IOptions<CentralServerOptions> options) : ITrustBindingFetcher
{
    private const int MaxResponseBytes = 16 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<(TrustBinding? Binding, byte[] PresentedCertificateSpkiSha256)> FetchAsync(CancellationToken cancellationToken)
    {
        byte[]? presented = null;
        using var handler = new SocketsHttpHandler
        {
            // Fresh connection per fetch, so the captured certificate belongs to this exact request.
            PooledConnectionLifetime = TimeSpan.Zero,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }

                    using var x509 = new X509Certificate2(certificate);
                    presented = TrustBindingFormat.ComputeSpkiHash(x509);
                    return true;
                },
            },
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = options.Value.BaseAddress,
            Timeout = Timeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

        using var response = await client.GetAsync(new Uri(ApiRoutes.TrustBinding, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var binding = await response.Content.ReadFromJsonAsync<TrustBinding>(cancellationToken).ConfigureAwait(false);
        return (binding, presented ?? throw new InvalidOperationException("The central server presented no TLS certificate."));
    }
}
