using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using Microsoft.Win32;

using SUSyphos.Server.Administration;

namespace SUSyphos.Server.Trust;

/// <summary>
/// A certificate from LocalMachine\My. <c>Unusable</c> says why the central server cannot present it (e.g. expired); null
/// for a certificate with private key, valid now and allowed for server authentication.
/// </summary>
internal sealed record CertificateCandidate(string Thumbprint, string Subject, string Issuer, DateTime NotBeforeUtc, DateTime NotAfterUtc, IReadOnlyList<string> DnsNames, bool SelfGenerated = false, string? Unusable = null);

/// <summary>The machine certificate store and the setting that selects the TLS certificate. Separate so tests never touch the machine.</summary>
internal interface IServerCertificateStore
{
    /// <summary>Server certificates with private key that are valid now, newest expiry first.</summary>
    IReadOnlyList<CertificateCandidate> ListCandidates();

    /// <summary>Every certificate of the store: the usable ones first, then the others with the reason they cannot be used.</summary>
    IReadOnlyList<CertificateCandidate> ListAll();

    /// <summary>
    /// The certificate with private key and this thumbprint, or null; also when expired or archived after a renewal, so a
    /// renewal is still found after a restart. The caller owns it.
    /// </summary>
    X509Certificate2? Load(string thumbprint);

    /// <summary>Persists the selection, so it survives restarts and upgrades.</summary>
    void SaveThumbprint(string thumbprint);

    /// <summary>Creates a self-signed server certificate for this machine with a non-exportable key and adds it to the store.</summary>
    X509Certificate2 CreateSelfSigned(TimeSpan lifetime);
}

/// <summary>LocalMachine\My and the installer setting HKLM\SOFTWARE\SUSyphos\Server\CertificateThumbprint.</summary>
internal sealed class WindowsServerCertificateStore : IServerCertificateStore
{
    private const int RsaKeySize = 3072;

    public IReadOnlyList<CertificateCandidate> ListCandidates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.UtcNow;
        var candidates = new List<CertificateCandidate>();
        foreach (var certificate in store.Certificates)
        {
            using (certificate)
            {
                if (ServerCertificates.UnusableReason(certificate, now) is null)
                {
                    candidates.Add(ServerCertificates.Describe(certificate));
                }
            }
        }

        return [.. candidates.OrderByDescending(c => c.NotAfterUtc)];
    }

    public IReadOnlyList<CertificateCandidate> ListAll()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.UtcNow;
        var all = new List<CertificateCandidate>();
        foreach (var certificate in store.Certificates)
        {
            using (certificate)
            {
                all.Add(ServerCertificates.Describe(certificate) with { Unusable = ServerCertificates.UnusableReason(certificate, now) ?? ServerCertificates.KeyProblem(certificate) });
            }
        }

        return ServerCertificates.Order(all);
    }

    public X509Certificate2? Load(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly | OpenFlags.IncludeArchived);
        X509Certificate2? found = null;
        foreach (var certificate in store.Certificates)
        {
            if (found is null && certificate.HasPrivateKey && certificate.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                found = certificate;
            }
            else
            {
                certificate.Dispose();
            }
        }

        return found;
    }

    public void SaveThumbprint(string thumbprint)
    {
        using var key = Registry.LocalMachine.CreateSubKey(SUSyphos.Server.ServerRegistry.KeyPath, writable: true);
        key.SetValue("CertificateThumbprint", thumbprint, RegistryValueKind.String);
    }

    public X509Certificate2 CreateSelfSigned(TimeSpan lifetime)
    {
        // A persisted machine key that cannot be exported; usable by administrators and the service (LocalSystem).
        var parameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.AllUsages,
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(RsaKeySize), CngPropertyOptions.None));
        using var key = CngKey.Create(CngAlgorithm.Rsa, $"SUSyphos-TLS-{Guid.NewGuid():N}", parameters);
        using var rsa = new RSACng(key);

        var now = DateTimeOffset.UtcNow;
        using var certificate = ServerCertificates.CreateRequest(MachineDnsNames(), rsa).CreateSelfSigned(now.AddMinutes(-10), now + lifetime);
        certificate.FriendlyName = ServerCertificates.SelfGeneratedFriendlyName;
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
        }

        return Load(certificate.Thumbprint) ?? throw new InvalidOperationException("The created certificate was not found in LocalMachine\\My.");
    }

    /// <summary>Fully qualified name first (it becomes the CN), then the host name.</summary>
    private static List<string> MachineDnsNames()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var host = properties.HostName.ToLowerInvariant();
        return string.IsNullOrEmpty(properties.DomainName) ? [host] : [$"{host}.{properties.DomainName.ToLowerInvariant()}", host];
    }
}

/// <summary>
/// Changing the TLS certificate of the central server (docs/03-communication-protocol.md, section 3.2): by an administrator
/// in the dashboard, or automatically after a renewal or the rollover of the own self-signed certificate. The new
/// certificate is presented at once, and the trust anchor signs a binding with the next serial, so agents accept it
/// without any change on their side. Every change is audited.
/// </summary>
internal sealed partial class CertificateAdministration(
    IServerCertificateStore store,
    ITlsCertificateProvider provider,
    CertificateLifecycle lifecycle,
    TrustBindingService bindings,
    AuditLog audit)
{
    public IReadOnlyList<CertificateCandidate> ListCandidates() => store.ListCandidates();

    public IReadOnlyList<CertificateCandidate> ListAll() => store.ListAll();

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
        await ApplyAsync(certificate, actor, "Certificate.Changed", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Follows a renewal in the store or rolls over the own certificate; true if the certificate changed.</summary>
    public async Task<bool> CheckRenewalAsync(CancellationToken cancellationToken)
    {
        if (lifecycle.Check(provider.Certificate) is not { } change)
        {
            return false;
        }

        await ApplyAsync(change.Certificate, AuditLog.SystemActor, change.Action, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Audits the change the service start made (a created, renewed or fallback certificate), if any.</summary>
    public async Task AuditStartupChangeAsync(CancellationToken cancellationToken)
    {
        _ = provider.Certificate;
        if (lifecycle.StartupChange is { } change)
        {
            await WriteAuditAsync(change.Certificate, AuditLog.SystemActor, change.Action, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyAsync(X509Certificate2 certificate, string actor, string action, CancellationToken cancellationToken)
    {
        provider.Replace(certificate);
        await bindings.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(certificate, actor, action, cancellationToken).ConfigureAwait(false);
    }

    private Task WriteAuditAsync(X509Certificate2 certificate, string actor, string action, CancellationToken cancellationToken) =>
        audit.WriteAsync(actor, action, certificate.Subject, $"{certificate.Thumbprint}, valid until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}", cancellationToken);

    [GeneratedRegex("^[0-9A-Fa-f]{40}$")]
    private static partial Regex ThumbprintRegex();
}
