using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>How remote actions are named and coloured in the dashboard; shared by the server page and the Approvals page.</summary>
internal static class AgentActionText
{
    /// <summary>Name of the action in lists and tasks.</summary>
    public static string Label(AgentActionKind kind) => kind switch
    {
        AgentActionKind.SearchNow => "Search for updates",
        AgentActionKind.FullResync => "Sync inventory",
        AgentActionKind.RestartService => "Restart agent service",
        AgentActionKind.RestartServer => "Restart server now",
        AgentActionKind.UploadLogs => "Request logs",
        AgentActionKind.RestartServerInWindow => "Restart server in maintenance window",
        AgentActionKind.CancelRestart => "Cancel restart",
        AgentActionKind.InstallNow => "Install now, restart right after",
        AgentActionKind.InstallNowRestartInWindow => "Install now, restart in maintenance window",
        AgentActionKind.Uninstall => "Uninstall, restart right after",
        AgentActionKind.UninstallRestartInWindow => "Uninstall, restart in maintenance window",
        AgentActionKind.UninstallInWindow => "Uninstall in maintenance window",
        AgentActionKind.UninstallAgent => "Uninstall agent (removal)",
        _ => kind.ToString(),
    };

    /// <summary>CSS class of the state pill.</summary>
    public static string StateClass(AgentActionState state) => state switch
    {
        AgentActionState.Succeeded => "ok",
        AgentActionState.Queued or AgentActionState.Delivered => "pend",
        AgentActionState.Failed or AgentActionState.Rejected or AgentActionState.Expired => "fail",
        _ => "neutral",
    };
}
