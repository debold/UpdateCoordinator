namespace SUSyphos.Server.Administration;

/// <summary>
/// Sorts audit actions into the areas the views of the audit log show (sign-ins, rollouts and approvals, settings
/// changes) by the start of their name, so an action added later lands in its area without a list to maintain.
/// </summary>
public static class AuditAreas
{
    public const string SignIns = "Sign-ins";
    public const string Rollouts = "Rollouts and approvals";
    public const string Settings = "Settings changes";

    private static readonly string[] SignInPrefixes = ["Login.", "Logout", "Setup."];

    private static readonly string[] SettingsPrefixes =
    [
        "Ring.", "RingTransition.", "StartRule.", "Group.", "AssignmentRule.", "Approval.", "Notifications.Changed", "Backup.ScheduleChanged",
        "WindowsSignIn.", "User.", "Installation.", "OneTimeWindow.", "Rollout.WithdrawnActionChanged", "Client.AssignmentChanged",
        "Client.RebootPolicyChanged", "Client.UpdateSourceChanged",
    ];

    private static readonly string[] RolloutPrefixes = ["Rollout.", "Update.", "Client.Approved", "Client.Rejected", "AgentPackage.Released", "AgentPackage.Withdrawn"];

    /// <summary>The area of an action; null for actions in none (e.g. remote actions, backups, removals).</summary>
    public static string? Of(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Starts(action, SignInPrefixes) ? SignIns
            : Starts(action, SettingsPrefixes) ? Settings
            : Starts(action, RolloutPrefixes) ? Rollouts
            : null;
    }

    private static bool Starts(string action, string[] prefixes) => prefixes.Any(p => action.StartsWith(p, StringComparison.Ordinal));
}
