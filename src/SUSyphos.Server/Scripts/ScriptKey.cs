using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Common;
using SUSyphos.Contracts.Scripts;
using SUSyphos.Contracts.Trust;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Trust;

namespace SUSyphos.Server.Scripts;

/// <summary>
/// The private script-signing key (Marc, 2026-11-13): ECDSA P-256 in a DPAPI-protected file next to the trust anchor,
/// in the encrypted backup like the anchor, created when the first script is approved. Without the file a new key is
/// created and certified; revisions signed before stay verifiable through the public keys in the database.
/// </summary>
internal sealed partial class ScriptKey : IDisposable
{
    private const string ProtectionPurpose = "SUSyphos.Server.ScriptKey.v1";
    public const string FileName = "script-key.key";

    private readonly ProtectedFile _file;
    private readonly ILogger<ScriptKey> _logger;
    private readonly Lock _gate = new();
    private ECDsa? _key;
    private bool _loaded;

    public ScriptKey(IOptions<TrustOptions> options, ILogger<ScriptKey> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _file = new ProtectedFile(PathFor(options.Value.AnchorPath), ProtectionPurpose, logger);
    }

    /// <summary>The file of the key: next to the anchor file.</summary>
    public static string PathFor(string anchorPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(anchorPath)) ?? ".", FileName);

    /// <summary>The key, or null while none exists (or the file cannot be read any more).</summary>
    public ECDsa? Current
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    AdoptRestored();
                    if (_file.Read() is { } pkcs8)
                    {
                        try
                        {
                            _key = ECDsa.Create();
                            _key.ImportPkcs8PrivateKey(pkcs8, out _);
                        }
                        catch (CryptographicException)
                        {
                            _key?.Dispose();
                            _key = null;
                            LogUnreadable(_file.Path);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(pkcs8);
                        }
                    }
                }

                return _key;
            }
        }
    }

    /// <summary>Creates a new key and stores it, replacing an unreadable one.</summary>
    public ECDsa CreateNew()
    {
        lock (_gate)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pkcs8 = key.ExportPkcs8PrivateKey();
            try
            {
                _file.Write(pkcs8);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }

            _key?.Dispose();
            _key = key;
            _loaded = true;
            LogCreated(_file.Path);
            return key;
        }
    }

    /// <summary>The private key as password-encrypted PKCS#8 for the backup; null without a key.</summary>
    public byte[]? ExportEncrypted(string password) =>
        Current?.ExportEncryptedPkcs8PrivateKey(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, TrustAnchor.ExportIterations));

    /// <summary>Stages a key restored from a backup for the service, protected for the machine like the anchor.</summary>
    public static void StoreRestored(string anchorPath, byte[] pkcs8) =>
        RestrictedFile.Write(PathFor(anchorPath) + TrustAnchor.RestoreSuffix, ProtectedData.Protect(pkcs8, Entropy, DataProtectionScope.LocalMachine));

    private static byte[] Entropy => System.Text.Encoding.UTF8.GetBytes(ProtectionPurpose);

    private void AdoptRestored()
    {
        var staged = _file.Path + TrustAnchor.RestoreSuffix;
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

            _file.Write(pkcs8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        File.Delete(staged);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _key?.Dispose();
            _key = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Created a new script-signing key at {Path}. It is part of the backup like the trust anchor.")]
    private partial void LogCreated(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "The script-signing key at {Path} cannot be read; a new key is created with the next approval.")]
    private partial void LogUnreadable(string path);
}

/// <summary>Signs approved script revisions with the active script key, certifying a new key with the trust anchor when needed.</summary>
internal sealed class ScriptSigning(ServerDbContext db, ScriptKey key, TrustAnchor anchor, AuditLog audit, TimeProvider timeProvider)
{
    /// <summary>The active key with its database entry; creates and certifies one when there is none or it was lost.</summary>
    public async Task<(ScriptKeyEntity Entry, ECDsa Key)> ActiveAsync(string actor, CancellationToken cancellationToken)
    {
        if (key.Current is { } current)
        {
            var spki = current.ExportSubjectPublicKeyInfo();
            var known = await db.ScriptKeys.Where(k => k.RetiredAtUtc == null).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (known.FirstOrDefault(k => k.PublicKey.AsSpan().SequenceEqual(spki)) is { } entry)
            {
                return (entry, current);
            }
        }

        var created = key.CreateNew();
        var now = DateTime.SpecifyKind(timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        var publicKey = created.ExportSubjectPublicKeyInfo();
        var keyId = $"k{now:yyyyMMdd}-{ScriptSigningFormat.Sha256(publicKey)[..8].ToLowerInvariant()}";
        foreach (var old in await db.ScriptKeys.Where(k => k.RetiredAtUtc == null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            old.RetiredAtUtc = now;
        }

        var newEntry = new ScriptKeyEntity
        {
            KeyId = keyId,
            PublicKey = publicKey,
            Certificate = anchor.Sign(ScriptSigningFormat.KeyCertificateBytes(keyId, publicKey, new DateTimeOffset(now))),
            CreatedAtUtc = now,
        };
        db.ScriptKeys.Add(newEntry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Script.KeyCreated", keyId, $"SHA-256 of the public key {ScriptSigningFormat.Sha256(publicKey)}", cancellationToken).ConfigureAwait(false);
        return (newEntry, created);
    }

    public static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, TrustBindingFormat.HashAlgorithm);

    /// <summary>The active key for display (fingerprint), without creating one.</summary>
    public Task<ScriptKeyEntity?> ActiveEntryAsync(CancellationToken cancellationToken) =>
        db.ScriptKeys.AsNoTracking().Where(k => k.RetiredAtUtc == null).OrderByDescending(k => k.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
}
