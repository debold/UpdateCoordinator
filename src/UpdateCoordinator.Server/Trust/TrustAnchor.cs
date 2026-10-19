using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using UpdateCoordinator.Common;
using UpdateCoordinator.Contracts.Trust;
using UpdateCoordinator.Server.Administration;

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

    /// <summary>Next to the anchor file: a restored key the service takes over on its next start.</summary>
    public const string RestoreSuffix = ".restore";

    /// <summary>
    /// Stages a restored key for the central server. The restore runs as an administrator, the service as LocalSystem, so
    /// DPAPI for the administrator's account would be unreadable for the service. The key is therefore protected for the
    /// machine in a file only administrators and LocalSystem can open; on its next start the service protects it for its
    /// own account at <paramref name="anchorPath"/> and deletes the staged file.
    /// </summary>
    public static void StoreRestored(string anchorPath, byte[] pkcs8) =>
        RestrictedFile.Write(anchorPath + RestoreSuffix, ProtectedData.Protect(pkcs8, Entropy, DataProtectionScope.LocalMachine));

    private static byte[] Entropy => System.Text.Encoding.UTF8.GetBytes(ProtectionPurpose);

    private static void AdoptRestored(ProtectedFile file, ILogger logger)
    {
        var staged = file.Path + RestoreSuffix;
        if (!File.Exists(staged))
        {
            return;
        }

        var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(staged), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            using (var check = ECDsa.Create())
            {
                check.ImportPkcs8PrivateKey(pkcs8, out _);
            }

            file.Write(pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        File.Delete(staged);
        LogRestored(logger, file.Path);
    }

    private static ECDsa LoadOrCreate(ProtectedFile file, ILogger logger)
    {
        AdoptRestored(file, logger);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Took over the trust anchor restored from a backup at {Path}.")]
    private static partial void LogRestored(ILogger logger, string path);
}
