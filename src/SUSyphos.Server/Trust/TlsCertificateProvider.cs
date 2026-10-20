using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Options;

namespace SUSyphos.Server.Trust;

/// <summary>
/// Supplies the TLS certificate the central server presents to agents.
/// </summary>
internal interface ITlsCertificateProvider
{
    X509Certificate2 Certificate { get; }

    /// <summary>Presents another certificate from now on (new TLS handshakes use it).</summary>
    void Replace(X509Certificate2 certificate);
}

/// <summary>
/// Holds the certificate from the Windows certificate store (chosen by <see cref="CertificateLifecycle"/>), so no certificate password has to be stored in configuration.
/// </summary>
internal sealed class StoreTlsCertificateProvider(IOptions<TrustOptions> options, IHostEnvironment environment, CertificateLifecycle lifecycle) : ITlsCertificateProvider, IDisposable
{
    /// <summary>Extension that marks the ASP.NET Core HTTPS development certificate.</summary>
    private const string DevelopmentCertificateOid = "1.3.6.1.4.1.311.84.1.1";

    private readonly Lock _gate = new();
    private X509Certificate2? _certificate;
    private X509Certificate2? _previous;

    public X509Certificate2 Certificate
    {
        get
        {
            lock (_gate)
            {
                return _certificate ??= Load();
            }
        }
    }

    public void Replace(X509Certificate2 certificate)
    {
        lock (_gate)
        {
            // The previous certificate may still be in use by handshakes in progress; it is released with the next change.
            _previous?.Dispose();
            _previous = _certificate;
            _certificate = certificate;
        }
    }

    private X509Certificate2 Load()
    {
        // A developer runs without administrator rights: certificates from CurrentUser\My, or the ASP.NET Core development certificate.
        if (environment.IsDevelopment())
        {
            var thumbprint = options.Value.CertificateThumbprint;
            return string.IsNullOrEmpty(thumbprint)
                ? Find(StoreLocation.CurrentUser, c => c.Extensions[DevelopmentCertificateOid] is not null)
                    ?? throw new InvalidOperationException("No ASP.NET Core development certificate found. Run 'dotnet dev-certs https --trust'.")
                : Find(StoreLocation.CurrentUser, c => c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase)) ?? lifecycle.SelectInitial();
        }

        return lifecycle.SelectInitial();
    }

    internal static X509Certificate2? Find(StoreLocation location, Func<X509Certificate2, bool> match)
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
        lock (_gate)
        {
            _previous?.Dispose();
            _certificate?.Dispose();
        }
    }
}
