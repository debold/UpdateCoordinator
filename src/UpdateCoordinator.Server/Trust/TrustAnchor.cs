using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using UpdateCoordinator.Common;
using UpdateCoordinator.Contracts.Trust;

namespace UpdateCoordinator.Server.Trust;

/// <summary>
/// The long-lived signing key of the central server (ECDSA P-256). Created on first start and kept
/// in a DPAPI-protected file. Agents pin its public key; it signs the binding to the TLS certificate.
/// </summary>
internal sealed partial class TrustAnchor : IDisposable
{
    private const string ProtectionPurpose = "UpdateCoordinator.Server.TrustAnchor.v1";

    private readonly Lazy<ECDsa> _key;

    public TrustAnchor(IOptions<TrustOptions> options, ILogger<TrustAnchor> logger)
    {
        var file = new ProtectedFile(options.Value.AnchorPath, ProtectionPurpose, logger);
        _key = new Lazy<ECDsa>(() => LoadOrCreate(file, logger));
    }

    /// <summary>Public key as SubjectPublicKeyInfo (DER); this is what agents pin.</summary>
    public byte[] PublicKey => _key.Value.ExportSubjectPublicKeyInfo();

    public byte[] Sign(byte[] data) => _key.Value.SignData(data, TrustBindingFormat.HashAlgorithm);

    public bool Verify(byte[] data, byte[] signature) => _key.Value.VerifyData(data, signature, TrustBindingFormat.HashAlgorithm);

    /// <summary>Iterations of the password-based encryption of the exported key (PBKDF2 with SHA-256, AES-256).</summary>
    public const int ExportIterations = 600_000;

    /// <summary>The private key as password-encrypted PKCS#8 for backups (docs/04-central-server.md, backup).</summary>
    public byte[] ExportEncrypted(string password) =>
        _key.Value.ExportEncryptedPkcs8PrivateKey(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, ExportIterations));

    /// <summary>
    /// Decrypts a backed-up key; returns it as PKCS#8 and its public key.
    /// Throws <see cref="CryptographicException"/> for a wrong password or damaged data.
    /// </summary>
    public static (byte[] Pkcs8, byte[] PublicKey) DecryptBackup(byte[] encrypted, string password)
    {
        using var key = ECDsa.Create();
        key.ImportEncryptedPkcs8PrivateKey(password, encrypted, out _);
        return (key.ExportPkcs8PrivateKey(), key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Stores a restored key DPAPI-protected at <paramref name="anchorPath"/>, where the central server loads it.</summary>
    public static void StoreRestored(string anchorPath, byte[] pkcs8, ILogger logger) =>
        new ProtectedFile(anchorPath, ProtectionPurpose, logger).Write(pkcs8);

    private static ECDsa LoadOrCreate(ProtectedFile file, ILogger logger)
    {
        var key = ECDsa.Create();
        var stored = file.Read();
        if (stored is not null)
        {
            try
            {
                key.ImportPkcs8PrivateKey(stored, out _);
                return key;
            }
            catch (CryptographicException)
            {
                // Never overwrite an existing but unreadable anchor silently: that would lock out every agent.
                key.Dispose();
                throw new InvalidOperationException($"The trust anchor at {file.Path} exists but cannot be loaded. Restore it from backup or remove it deliberately.");
            }
        }

        if (File.Exists(file.Path))
        {
            key.Dispose();
            throw new InvalidOperationException($"The trust anchor at {file.Path} exists but cannot be decrypted (wrong service account?). Restore it from backup or remove it deliberately.");
        }

        key.GenerateKey(ECCurve.NamedCurves.nistP256);
        file.Write(key.ExportPkcs8PrivateKey());
        LogCreated(logger, file.Path);
        return key;
    }

    public void Dispose()
    {
        if (_key.IsValueCreated)
        {
            _key.Value.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Created a new trust anchor at {Path}. Include this file in backups: without it all agents must be reset.")]
    private static partial void LogCreated(ILogger logger, string path);
}
