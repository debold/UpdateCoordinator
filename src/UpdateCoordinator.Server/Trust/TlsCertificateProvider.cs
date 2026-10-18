using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Options;

namespace UpdateCoordinator.Server.Trust;

/// <summary>
/// Supplies the TLS certificate the central server presents to agents.
/// </summary>
internal interface ITlsCertificateProvider
{
    X509Certificate2 Certificate { get; }
}

/// <summary>
/// Loads the certificate from the Windows certificate store, so no certificate password has to be stored in configuration.
/// </summary>
internal sealed class StoreTlsCertificateProvider(IOptions<TrustOptions> options, IHostEnvironment environment) : ITlsCertificateProvider, IDisposable
{
    /// <summary>Extension that marks the ASP.NET Core HTTPS development certificate.</summary>
    private const string DevelopmentCertificateOid = "1.3.6.1.4.1.311.84.1.1";

    private readonly Lazy<X509Certificate2> _certificate = new(() => Load(options.Value.CertificateThumbprint, environment));

    public X509Certificate2 Certificate => _certificate.Value;

    private static X509Certificate2 Load(string? thumbprint, IHostEnvironment environment)
    {
        if (!string.IsNullOrEmpty(thumbprint))
        {
            return Find(StoreLocation.LocalMachine, c => c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
                ?? Find(StoreLocation.CurrentUser, c => c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No valid TLS certificate with private key and thumbprint {thumbprint} was found in LocalMachine\\My or CurrentUser\\My.");
        }

        if (environment.IsDevelopment())
        {
            return Find(StoreLocation.CurrentUser, c => c.Extensions[DevelopmentCertificateOid] is not null)
                ?? throw new InvalidOperationException("No ASP.NET Core development certificate found. Run 'dotnet dev-certs https --trust'.");
        }

        throw new InvalidOperationException("No TLS certificate configured. Set Trust:CertificateThumbprint to a certificate in LocalMachine\\My.");
    }

    private static X509Certificate2? Find(StoreLocation location, Func<X509Certificate2, bool> match)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        X509Certificate2? best = null;
        foreach (var certificate in store.Certificates)
        {
            if (best is null
                && match(certificate)
                && certificate.HasPrivateKey
                && certificate.NotBefore <= now
                && certificate.NotAfter > now)
            {
                best = certificate;
            }
            else
            {
                certificate.Dispose();
            }
        }

        return best;
    }

    public void Dispose()
    {
        if (_certificate.IsValueCreated)
        {
            _certificate.Value.Dispose();
        }
    }
}
