using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UpdateCoordinator.Agent.Identity;

/// <summary>
/// Stores the identity in a file encrypted with DPAPI for the service account (CurrentUser scope),
/// so only processes running as that account can read the client secret.
/// </summary>
internal sealed partial class DpapiAgentIdentityStore(string filePath, ILogger<DpapiAgentIdentityStore> logger) : IAgentIdentityStore
{
    /// <summary>Default location: %ProgramData%\UpdateCoordinator\Agent\identity.dat.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UpdateCoordinator",
        "Agent",
        "identity.dat");

    // Additional entropy binds the ciphertext to this purpose; it is not a secret.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UpdateCoordinator.Agent.Identity.v1");

    public AgentIdentity? Load()
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(filePath), Entropy, DataProtectionScope.CurrentUser);
            var identity = JsonSerializer.Deserialize<AgentIdentity>(plain);
            if (identity is null || identity.ClientId == Guid.Empty || string.IsNullOrEmpty(identity.ClientSecret))
            {
                LogUnreadable(filePath, "invalid content");
                return null;
            }

            return identity;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            LogUnreadable(filePath, ex.GetType().Name);
            return null;
        }
    }

    public void Save(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(identity), Entropy, DataProtectionScope.CurrentUser);

        // Write to a temporary file first so a crash never leaves a half-written identity behind.
        var temporary = filePath + ".tmp";
        File.WriteAllBytes(temporary, cipher);
        File.Move(temporary, filePath, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored agent identity at {Path} cannot be read ({Reason}); the agent will register again.")]
    private partial void LogUnreadable(string path, string reason);
}
