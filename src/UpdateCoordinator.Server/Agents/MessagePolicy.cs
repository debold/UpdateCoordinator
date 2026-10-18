using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Agent message types (see the whitelist in docs/03-communication-protocol.md, section 5).
/// Further types are added with their features.
/// </summary>
internal enum MessageType
{
    Register,
    Heartbeat,
}

/// <summary>
/// Whitelist of allowed message types per registration state. A null state means "not registered yet".
/// </summary>
internal static class MessagePolicy
{
    public static bool IsAllowed(MessageType messageType, RegistrationState? state) => messageType switch
    {
        MessageType.Register => state is null,
        MessageType.Heartbeat => state is RegistrationState.Pending or RegistrationState.Approved,
        _ => false,
    };
}
