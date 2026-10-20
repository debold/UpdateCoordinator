using SUSyphos.Common;

namespace SUSyphos.Agent.Identity;

/// <summary>
/// Stores the identity in a DPAPI-protected file, readable only by the service account.
/// </summary>
internal sealed partial class DpapiAgentIdentityStore(string filePath, ILogger<DpapiAgentIdentityStore> logger) : IAgentIdentityStore
{
    /// <summary>Default location: %ProgramData%\SUSyphos\Agent\identity.dat.</summary>
    public static string DefaultPath { get; } = Path.Combine(DataPaths.Agent, "identity.dat");

    private readonly ProtectedJsonFile<AgentIdentity> _file = new(
        new ProtectedFile(filePath, "UpdateCoordinator.Agent.Identity.v1", logger),
        logger);

    public AgentIdentity? Load()
    {
        var identity = _file.Load();
        if (identity is not null && (identity.ClientId == Guid.Empty || string.IsNullOrEmpty(identity.ClientSecret)))
        {
            LogInvalid(filePath);
            return null;
        }

        return identity;
    }

    public void Save(AgentIdentity identity) => _file.Save(identity);

    public void Delete() => _file.Delete();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored agent identity at {Path} is incomplete; the agent will register again.")]
    private partial void LogInvalid(string path);
}
