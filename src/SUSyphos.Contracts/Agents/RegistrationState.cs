using System.Text.Json.Serialization;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// Registration state of a client as seen by the central server (see docs/03-communication-protocol.md).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegistrationState>))]
public enum RegistrationState
{
    /// <summary>Registered, waiting for manual or rule-based approval.</summary>
    Pending = 0,

    /// <summary>Approved; receives control information.</summary>
    Approved = 1,

    /// <summary>Approval was rejected by an admin.</summary>
    Rejected = 2,

    /// <summary>Blocked until an admin resolves a clone suspicion.</summary>
    Blocked = 3,

    /// <summary>Unregistered or soft-deleted.</summary>
    Deleted = 4,
}
