using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Creates and verifies client secrets. Secrets are 256-bit random values, so a single SHA-256 hash
/// is sufficient for storage; a slow password hash is not needed for high-entropy secrets.
/// </summary>
internal static class ClientSecrets
{
    private const int SecretBytes = 32;

    /// <summary>Upper bound for a presented secret; longer values are rejected before hashing.</summary>
    public const int MaxSecretLength = 64;

    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public static bool Verify(string presented, byte[] storedHash)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length > MaxSecretLength)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Hash(presented), storedHash);
    }
}
