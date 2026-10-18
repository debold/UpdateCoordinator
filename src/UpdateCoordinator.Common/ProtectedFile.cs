using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

namespace UpdateCoordinator.Common;

/// <summary>
/// A small file encrypted with DPAPI for the account the service runs as (CurrentUser scope),
/// so only processes running as that account can read it. Used for client credentials,
/// trust state and the trust anchor key.
/// </summary>
public sealed partial class ProtectedFile
{
    private readonly byte[] _entropy;
    private readonly ILogger _logger;

    /// <param name="path">Full path of the file.</param>
    /// <param name="purpose">Binds the ciphertext to one purpose so files cannot be swapped; not a secret.</param>
    /// <param name="logger">Receives a warning when the file cannot be read.</param>
    public ProtectedFile(string path, string purpose, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        Path = path;
        _entropy = Encoding.UTF8.GetBytes(purpose);
        _logger = logger;
    }

    public string Path { get; }

    /// <summary>Returns the decrypted content, or null if the file is missing or cannot be decrypted.</summary>
    public byte[]? Read()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(Path), _entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            LogUnreadable(Path, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Encrypts and writes the content atomically (temporary file, then move).</summary>
    public void Write(ReadOnlySpan<byte> content)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var cipher = ProtectedData.Protect(content.ToArray(), _entropy, DataProtectionScope.CurrentUser);
        var temporary = Path + ".tmp";
        File.WriteAllBytes(temporary, cipher);
        File.Move(temporary, Path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Protected file {Path} cannot be read ({Reason}).")]
    private partial void LogUnreadable(string path, string reason);
}
