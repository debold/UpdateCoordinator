namespace SUSyphos.Server.Notifications;

/// <summary>How important an event is: critical and recommended events are sent by default, optional ones only on request (Marc, 2026-10-23).</summary>
internal enum NotificationCategory
{
    /// <summary>Operation or security at risk.</summary>
    Critical,

    /// <summary>Needs an action, but not urgently.</summary>
    Recommended,

    /// <summary>For information.</summary>
    Optional,
}

/// <param name="Kind">The event kind.</param>
/// <param name="Category">Critical, recommended or optional.</param>
/// <param name="Label">Name in the event table and the e-mail heading.</param>
/// <param name="Description">One sentence in the event table: when it is sent.</param>
internal sealed record NotificationKindInfo(NotificationKind Kind, NotificationCategory Category, string Label, string Description);

/// <summary>Every event kind with its category, name and description: the table on the Notifications page and the e-mail headings.</summary>
internal static class NotificationCatalog
{
    public static readonly IReadOnlyList<NotificationKindInfo> All =
    [
        new(NotificationKind.RolloutAlarm, NotificationCategory.Critical, "Rollout alarm", "The success rate of an update in a ring is below the threshold; the rollout stops before the next ring."),
        new(NotificationKind.UpdateWithdrawn, NotificationCategory.Critical, "Update withdrawn by Microsoft", "Servers report that Windows Update no longer knows a released update."),
        new(NotificationKind.CloneSuspected, NotificationCategory.Critical, "Server blocked, suspected clone", "The same credentials came from two host names; the server is blocked until an administrator decides."),
        new(NotificationKind.LeasesHalted, NotificationCategory.Critical, "Coordinated group stopped", "Installations in a coordinated group stopped after failures; an operator must resume them."),
        new(NotificationKind.CertificateExpiring, NotificationCategory.Critical, "Certificate expiring", "The TLS certificate of the central server expires soon and no renewal was found."),
        new(NotificationKind.BackupFailed, NotificationCategory.Critical, "Backup failed", "The scheduled backup failed."),
        new(NotificationKind.ServerOffline, NotificationCategory.Critical, "Server offline", "An approved server sent no heartbeat for longer than the time set below."),
        new(NotificationKind.TrustReset, NotificationCategory.Critical, "Trust reset on a server", "An agent reports that its trust in the central server was reset locally (registry value TrustResetToken)."),
        new(NotificationKind.DefenderSignaturesOutdated, NotificationCategory.Critical, "Defender signatures out of date", "A server that receives antivirus definition updates has installed none for more than 48 hours."),
        new(NotificationKind.ServerPendingApproval, NotificationCategory.Recommended, "Server waiting for approval", "A new server registered and waits for approval."),
        new(NotificationKind.GateWaiting, NotificationCategory.Recommended, "Gate waiting for approval", "An update waits at a manual gate before the next ring."),
        new(NotificationKind.AgentUpdateFailed, NotificationCategory.Recommended, "Agent update failed", "An agent could not install a released agent version."),
        new(NotificationKind.InstallationFailed, NotificationCategory.Recommended, "Installation failed", "An update failed on a server that still needs it."),
        new(NotificationKind.PreFlightFailed, NotificationCategory.Recommended, "Pre-flight check failed", "A pre-flight check failed on a server (for example disk space or the Windows Update service); it installs nothing until fixed."),
        new(NotificationKind.RebootPendingLong, NotificationCategory.Recommended, "Restart pending for long", "A server has been waiting for a restart for longer than the days set below."),
        new(NotificationKind.InstalledOutside, NotificationCategory.Recommended, "Installation outside SUSyphos", "Windows or an administrator installed an update outside SUSyphos, or Windows is set to install on its own."),
        new(NotificationKind.SupportEnding, NotificationCategory.Recommended, "Support ending", "The Windows version of a server reaches its end of support within the warning days (180 by default), or has passed it; sent again when it gets close (30 days) and when it ends."),
        new(NotificationKind.CertificateChanged, NotificationCategory.Recommended, "Certificate changed", "The central server switched to another TLS certificate."),
        new(NotificationKind.ServerVersionAvailable, NotificationCategory.Recommended, "New central server version", "A newer central server release is available."),
        new(NotificationKind.AdminChanged, NotificationCategory.Recommended, "Administration changed", "An account was created, deleted or got another role, or an AD group was mapped or removed."),
        new(NotificationKind.AgentVersionAvailable, NotificationCategory.Optional, "New agent version", "A newer agent version is available and waits for release."),
        new(NotificationKind.RolloutProgress, NotificationCategory.Optional, "Rollout reached a ring", "An update was released to another ring."),
        new(NotificationKind.PauseChanged, NotificationCategory.Optional, "Pause or blackout", "All installations were paused or resumed, or a blackout period started."),
        new(NotificationKind.NewUpdatesDaily, NotificationCategory.Optional, "New updates (daily)", "Once a day: the updates the servers reported for the first time the day before."),
        new(NotificationKind.DailySummary, NotificationCategory.Optional, "Daily summary", "Once a day: compliance, failures and upcoming installations."),
        new(NotificationKind.WeeklySummary, NotificationCategory.Optional, "Weekly summary", "Every Monday: compliance, failures and upcoming installations."),
    ];

    public static NotificationKindInfo Of(NotificationKind kind) => All.First(k => k.Kind == kind);

    /// <summary>The kinds sent when nothing was chosen: critical and recommended.</summary>
    public static IReadOnlyList<NotificationKind> Defaults { get; } = [.. All.Where(k => k.Category != NotificationCategory.Optional).Select(k => k.Kind)];

    /// <summary>"Critical", "Warning" or "Info" for the tag in e-mails and the webhook.</summary>
    public static string Severity(NotificationCategory category) => category switch
    {
        NotificationCategory.Critical => "Critical",
        NotificationCategory.Recommended => "Warning",
        _ => "Info",
    };
}
