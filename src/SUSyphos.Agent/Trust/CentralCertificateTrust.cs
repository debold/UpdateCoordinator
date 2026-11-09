using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Options;

using SUSyphos.Common;

namespace SUSyphos.Agent.Trust;

/// <summary>Software settings of the agent (section <c>Software</c>, set centrally).</summary>
internal sealed class SoftwareAgentOptions
{
    public const string SectionName = "Software";

    /// <summary>
    /// Put the central server's own self-signed certificate into the trusted root certificates of the machine, so WinGet
    /// accepts the WinGet source of the central server (opt-in, Marc 2026-11-08).
    /// </summary>
    public bool TrustCentralCertificate { get; set; }
}

/// <summary>The certificate the central server presented and the trust anchor confirmed.</summary>
internal interface IAcceptedCertificateSource
{
    /// <summary>DER of the last accepted certificate; null until one was accepted.</summary>
    byte[]? AcceptedCertificate { get; }
}

/// <summary>Where the agent puts the certificate, and which one it put there. Separate so tests never touch the machine.</summary>
internal interface ICentralCertificateStore
{
    /// <summary>Thumbprint of the certificate the agent added, from its own note; null if none.</summary>
    string? AddedThumbprint { get; set; }

    bool Contains(string thumbprint);

    void Add(X509Certificate2 certificate);

    void Remove(string thumbprint);
}

/// <summary>
/// Trusts the central server's certificate for WinGet (docs/05-agent-design.md, software installation): WinGet checks the
/// certificate of its source like Windows does, not through the trust anchor. With the opt-in the agent adds exactly the
/// certificate it has verified through its pinned trust anchor, and only when it is self-signed (a certificate of a CA is
/// trusted already, and nothing it did not verify is ever added). A self-signed certificate is no CA certificate and
/// cannot issue others. When the certificate changes, the opt-in is switched off or the agent is uninstalled, the agent
/// removes what it added, recognised by its own note.
/// </summary>
internal sealed partial class CentralCertificateTrust(IAcceptedCertificateSource trust, IOptionsMonitor<SoftwareAgentOptions> options, ICentralCertificateStore store, ILogger<CentralCertificateTrust> logger)
{
    /// <summary>Applies the central setting; call once per cycle and with false on uninstall.</summary>
    public void Apply() => Apply(options.CurrentValue.TrustCentralCertificate);

    public void Apply(bool enabled)
    {
        var wanted = enabled ? Wanted() : null;
        var added = store.AddedThumbprint;
        if (added is not null && !string.Equals(added, wanted?.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            store.Remove(added);
            store.AddedThumbprint = null;
            LogRemoved(added);
        }

        if (wanted is null)
        {
            return;
        }

        using (wanted)
        {
            if (store.AddedThumbprint is null && !store.Contains(wanted.Thumbprint))
            {
                store.Add(wanted);
                store.AddedThumbprint = wanted.Thumbprint;
                LogAdded(wanted.Thumbprint, wanted.Subject);
            }
        }
    }

    /// <summary>The verified certificate if it is self-signed and not trusted by Windows yet; null otherwise.</summary>
    private X509Certificate2? Wanted()
    {
        if (trust.AcceptedCertificate is not { } raw)
        {
            return null;
        }

        var certificate = X509CertificateLoader.LoadCertificate(raw);
        if (!string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal) || IsCertificateAuthority(certificate))
        {
            // Issued by a CA: trusting it is the CA's job; a CA certificate is never added.
            certificate.Dispose();
            return null;
        }

        return certificate;
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(c => c.CertificateAuthority);

    [LoggerMessage(Level = LogLevel.Information, Message = "Added the central server's certificate {Thumbprint} ({Subject}) to the trusted root certificates for WinGet (opt-in).")]
    private partial void LogAdded(string thumbprint, string subject);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the central server's certificate {Thumbprint} the agent had added to the trusted root certificates.")]
    private partial void LogRemoved(string thumbprint);
}

/// <summary>LocalMachine\Root and a note file in the agent's data folder.</summary>
internal sealed class MachineRootCertificateStore : ICentralCertificateStore
{
    private static readonly string NotePath = Path.Combine(DataPaths.Agent, "trusted-central-certificate.txt");

    public string? AddedThumbprint
    {
        get => File.Exists(NotePath) ? File.ReadAllText(NotePath).Trim() is { Length: 40 } thumbprint && thumbprint.All(char.IsAsciiHexDigit) ? thumbprint : null : null;
        set
        {
            if (value is null)
            {
                File.Delete(NotePath);
            }
            else
            {
                Directory.CreateDirectory(DataPaths.Agent);
                File.WriteAllText(NotePath, value);
            }
        }
    }

    public bool Contains(string thumbprint)
    {
        using var root = Open(OpenFlags.ReadOnly);
        var found = root.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        var contains = found.Count > 0;
        foreach (var certificate in found)
        {
            certificate.Dispose();
        }

        return contains;
    }

    public void Add(X509Certificate2 certificate)
    {
        using var root = Open(OpenFlags.ReadWrite);
        root.Add(certificate);
    }

    public void Remove(string thumbprint)
    {
        using var root = Open(OpenFlags.ReadWrite);
        foreach (var certificate in root.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            root.Remove(certificate);
            certificate.Dispose();
        }
    }

    private static X509Store Open(OpenFlags flags)
    {
        var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(flags);
        return store;
    }
}
