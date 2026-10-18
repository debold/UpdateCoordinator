namespace UpdateCoordinator.Agent.Identity;

/// <summary>
/// Persists the agent identity between service restarts.
/// </summary>
internal interface IAgentIdentityStore
{
    /// <summary>Returns the stored identity, or null if none exists or it cannot be read.</summary>
    AgentIdentity? Load();

    void Save(AgentIdentity identity);

    void Delete();
}
