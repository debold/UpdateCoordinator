using System.Text.Json.Serialization;

namespace SUSyphos.Contracts.Agents;

/// <summary>
/// Whether the agent may restart the server after updates (docs/05-agent-design.md, section 4).
/// The agent never reboots unless the central server allows it and no local veto applies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RebootPolicy>))]
public enum RebootPolicy
{
    /// <summary>Only report that a reboot is pending.</summary>
    Never = 0,

    /// <summary>Reboot inside a maintenance window when a reboot is pending.</summary>
    InsideMaintenanceWindow = 1,
}
