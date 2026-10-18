using UpdateCoordinator.Common;

namespace UpdateCoordinator.Agent.Trust;

/// <summary>
/// Persists the trust state between service restarts.
/// </summary>
internal interface ITrustStateStore
{
    TrustState Load();

    void Save(TrustState state);
}

/// <summary>
/// DPAPI-protected trust state; an unreadable file is treated like a first contact.
/// </summary>
internal sealed class ProtectedTrustStateStore(string filePath, ILogger<ProtectedTrustStateStore> logger) : ITrustStateStore
{
    /// <summary>Default location: %ProgramData%\UpdateCoordinator\Agent\trust.dat.</summary>
    public static string DefaultPath { get; } = Path.Combine(DataPaths.Agent, "trust.dat");

    private readonly ProtectedJsonFile<TrustState> _file = new(
        new ProtectedFile(filePath, "UpdateCoordinator.Agent.Trust.v1", logger),
        logger);

    public TrustState Load() => _file.Load() ?? TrustState.Empty;

    public void Save(TrustState state) => _file.Save(state);
}
