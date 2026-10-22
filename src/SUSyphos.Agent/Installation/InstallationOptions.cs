using System.ComponentModel.DataAnnotations;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Installation;

/// <summary>
/// A maintenance window as bound from local configuration (mutable for the configuration binder).
/// Converted to the shared <see cref="MaintenanceWindow"/> contract for validation and planning.
/// </summary>
internal sealed class MaintenanceWindowRule
{
    public List<DayOfWeek> Days { get; set; } = [];

    public TimeSpan Start { get; set; }

    /// <summary>End of the window; up to 1.00:00:00 (end of day).</summary>
    public TimeSpan End { get; set; }

    public MaintenanceWindow ToContract() => new() { Days = Days, Start = Start, End = End };
}

/// <summary>
/// Installation settings. Bound from the "Installation" configuration section.
/// Maintenance windows sent by the central server take precedence over the local ones.
/// </summary>
internal sealed class InstallationOptions : IValidatableObject
{
    public const string SectionName = "Installation";

    /// <summary>Local veto: false forbids reboots on this server whatever the central server allows (registry value AllowReboot).</summary>
    public bool AllowReboot { get; set; } = true;

    /// <summary>Warning to signed-in users before the agent restarts the server; central setting "restart-warning".</summary>
    [Range(typeof(TimeSpan), "00:01:00", "00:30:00")]
    public TimeSpan RestartWarning { get; set; } = RebootPlanner.WarningPeriod;

    /// <summary>Message shown to signed-in users with the restart warning; null for the built-in text. Central setting "restart-message".</summary>
    [StringLength(200)]
    public string? RestartMessage { get; set; }

    /// <summary>False disables installations completely (search and reporting continue).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local maintenance windows, used only while the central server sends none; empty means the system default.</summary>
    public List<MaintenanceWindowRule> Windows { get; set; } = [];

    /// <summary>Releases due within this time after a due batch are installed together with it, to avoid extra reboots.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "04:00:00")]
    public TimeSpan PullForwardTolerance { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>An update that was already attempted is not tried again before this time has passed.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Local windows, or the system default if none are configured.</summary>
    public IReadOnlyList<MaintenanceWindow> LocalWindows =>
        Windows.Count > 0 ? [.. Windows.Select(w => w.ToContract())] : MaintenanceWindow.SystemDefault;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var window in Windows.Select(w => w.ToContract()))
        {
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(window, new ValidationContext(window), results, validateAllProperties: true))
            {
                foreach (var result in results)
                {
                    yield return new ValidationResult(result.ErrorMessage, [nameof(Windows)]);
                }
            }
        }
    }
}
