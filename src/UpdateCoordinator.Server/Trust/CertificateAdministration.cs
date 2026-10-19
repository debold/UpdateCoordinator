using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using Microsoft.Win32;

using UpdateCoordinator.Server.Administration;

namespace UpdateCoordinator.Server.Trust;

/// <summary>A certificate the central server could present: from LocalMachine\My, with private key, valid now.</summary>
internal sealed record CertificateCandidate(string Thumbprint, string Subject, string Issuer, DateTime NotBeforeUtc, DateTime NotAfterUtc, IReadOnlyList<string> DnsNames);

/// <summary>The machine certificate store and the setting that selects the TLS certificate. Separate so tests never touch the machine.</summary>
internal interface IServerCertificateStore
{
    IReadOnlyList<CertificateCandidate> ListCandidates();

    /// <summary>The certificate with private key and this thumbprint, or null; the caller owns it.</summary>
    X509Certificate2? Load(string thumbprint);

    /// <summary>Persists the selection, so it survives restarts and upgrades.</summary>
    void SaveThumbprint(string thumbprint);
}

/// <summary>LocalMachine\My and the installer setting HKLM\SOFTWARE\UpdateCoordinator\Server\CertificateThumbprint.</summary>
internal sealed class WindowsServerCertificateStore : IServerCertificateStore
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    public IReadOnlyList<CertificateCandidate> ListCandidates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        var candidates = new List<CertificateCandidate>();
        foreach (var certificate in store.Certificates)
        {
            using (certificate)
            {
                if (certificate.HasPrivateKey && certificate.NotBefore <= now && certificate.NotAfter > now && AllowsServerAuthentication(certificate))
                {
                    candidates.Add(new CertificateCandidate(
                        certificate.Thumbprint,
                        certificate.Subject,
                        certificate.Issuer,
                        certificate.NotBefore.ToUniversalTime(),
                        certificate.NotAfter.ToUniversalTime(),
                        DnsNames(certificate)));
                }
            }
        }

        return [.. candidates.OrderByDescending(c => c.NotAfterUtc)];
    }

    public X509Certificate2? Load(string thumbprint) =>
        StoreTlsCertificateProvider.Find(StoreLocation.LocalMachine, c => c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));

    public void SaveThumbprint(string thumbprint)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UpdateCoordinator.Server.ServerRegistry.KeyPath, writable: true);
        key.SetValue("CertificateThumbprint", thumbprint, RegistryValueKind.String);
    }

    private static bool AllowsServerAuthentication(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage is null || usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == ServerAuthentication);
    }

    private static List<string> DnsNames(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.EnumerateDnsNames().ToList() ?? [];
}

/// <summary>
/// Changing the TLS certificate of the central server from the dashboard (docs/03-communication-protocol.md, section 3.2):
/// the new certificate is presented at once, and the trust anchor signs a binding with the next serial, so agents accept it
/// without any change on their side. Only an administrator can change it; the change is audited.
/// </summary>
internal sealed partial class CertificateAdministration(
    IServerCertificateStore store,
    ITlsCertificateProvider provider,
    TrustBindingService bindings,
    AuditLog audit)
{
    public IReadOnlyList<CertificateCandidate> ListCandidates() => store.ListCandidates();

    public X509Certificate2 Current => provider.Certificate;

    public async Task<Outcome> UseAsync(string? thumbprint, string actor, CancellationToken cancellationToken)
    {
        if (thumbprint is null || !ThumbprintRegex().IsMatch(thumbprint))
        {
            return new Outcome("The thumbprint must be 40 hexadecimal characters.");
        }

        if (string.Equals(provider.Certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return new Outcome("This certificate is already in use.");
        }

        if (!store.ListCandidates().Any(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)))
        {
            return new Outcome("The certificate is not a valid server certificate with private key in LocalMachine\\My.");
        }

        var certificate = store.Load(thumbprint);
        if (certificate is null)
        {
            return new Outcome("The certificate could not be loaded.");
        }

        store.SaveThumbprint(certificate.Thumbprint);
        provider.Replace(certificate);
        await bindings.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Certificate.Changed", certificate.Subject, $"{certificate.Thumbprint}, valid until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    [GeneratedRegex("^[0-9A-Fa-f]{40}$")]
    private static partial Regex ThumbprintRegex();
}
