using System.Globalization;

namespace SUSyphos.Contracts.Agents;

/// <summary>How a central agent setting is entered and stored.</summary>
public enum AgentSettingKind
{
    /// <summary>A duration entered as a whole number of <see cref="AgentSettingDefinition.Unit"/> (seconds, minutes or hours).</summary>
    Duration,

    /// <summary>A whole number within the range.</summary>
    Number,

    /// <summary>On or off.</summary>
    Flag,

    /// <summary>One of the given choices.</summary>
    Choice,

    /// <summary>A short text without control characters.</summary>
    Text,
}

/// <summary>
/// One agent setting the central server can set for all agents, a group or one server (Marc, 2026-10-22). The agent puts
/// the value at <see cref="ConfigurationPath"/> into its configuration, above its own files but below the local registry
/// (policy), and applies it without restart; logging settings restart the agent service.
/// </summary>
/// <param name="Key">Name in the dashboard and on the wire.</param>
/// <param name="Section">Box the setting appears in.</param>
/// <param name="Label">Field label.</param>
/// <param name="ConfigurationPath">Configuration key of the agent, e.g. "CentralServer:HeartbeatInterval".</param>
/// <param name="Kind">How the value is entered.</param>
/// <param name="Unit">"seconds", "minutes", "hours", "MB" or empty.</param>
/// <param name="Minimum">Lowest number (durations in <paramref name="Unit"/>).</param>
/// <param name="Maximum">Highest number, or the maximum text length.</param>
/// <param name="AgentDefault">What the agent uses when nothing is set, as entered in the dashboard.</param>
/// <param name="Choices">Allowed values of a choice.</param>
public sealed record AgentSettingDefinition(
    string Key,
    string Section,
    string Label,
    string ConfigurationPath,
    AgentSettingKind Kind,
    string Unit,
    int Minimum,
    int Maximum,
    string AgentDefault,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>True if a change only takes effect after the agent service restarted (the agent restarts itself): logging and process priority.</summary>
    public bool NeedsRestart => ConfigurationPath.StartsWith("Logging:", StringComparison.Ordinal) || ConfigurationPath == "Resources:LowPriority";

    /// <summary>
    /// The value as stored and delivered (durations and numbers as whole numbers in <see cref="Unit"/>, flags as "true" or
    /// "false"), or null if <paramref name="value"/> is not valid for this setting.
    /// </summary>
    public string? Normalize(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return Kind switch
        {
            AgentSettingKind.Duration or AgentSettingKind.Number =>
                int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= Minimum && number <= Maximum
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : null,
            AgentSettingKind.Flag => text.ToLowerInvariant() switch { "true" or "on" => "true", "false" or "off" => "false", _ => null },
            AgentSettingKind.Choice => Choices?.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase)),
            _ => text.Length <= Maximum && !text.Any(char.IsControl) && !text.Contains('"', StringComparison.Ordinal) ? text : null,
        };
    }

    /// <summary>
    /// The value the agent has in its configuration, in the form the dashboard shows (durations as whole numbers of
    /// <see cref="Unit"/>), without range checks; null if it cannot be read.
    /// </summary>
    public string? FromConfiguration(string? configured)
    {
        var text = configured?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (Kind != AgentSettingKind.Duration)
        {
            return Kind == AgentSettingKind.Flag ? text.ToLowerInvariant() : text;
        }

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var duration))
        {
            return null;
        }

        var amount = Unit switch { "seconds" => duration.TotalSeconds, "minutes" => duration.TotalMinutes, _ => duration.TotalHours };
        return amount.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>The normalized value in the form the agent's configuration binds (durations as hh:mm:ss).</summary>
    public string ToConfiguration(string normalized) => Kind == AgentSettingKind.Duration
        ? (Unit switch
        {
            "seconds" => TimeSpan.FromSeconds(int.Parse(normalized, CultureInfo.InvariantCulture)),
            "minutes" => TimeSpan.FromMinutes(int.Parse(normalized, CultureInfo.InvariantCulture)),
            _ => TimeSpan.FromHours(int.Parse(normalized, CultureInfo.InvariantCulture)),
        }).ToString("c", CultureInfo.InvariantCulture)
        : normalized;
}

/// <summary>The agent settings the central server manages; their ranges stay within what the agent accepts.</summary>
public static class AgentSettings
{
    public const string Intervals = "Intervals";
    public const string Logging = "Logging";
    public const string Resources = "Resources";
    public const string PreFlight = "Pre-flight check";
    public const string Search = "Update search";
    public const string RestartNotice = "Restart notice";

    /// <summary>Upper bound of entries a level (system, group, server) may set; one per setting.</summary>
    public static int MaxEntries => All.Count;

    public static IReadOnlyList<AgentSettingDefinition> All { get; } =
    [
        new("heartbeat-interval", Intervals, "Heartbeat", "CentralServer:HeartbeatInterval", AgentSettingKind.Duration, "seconds", 5, 600, "10"),
        new("search-interval", Intervals, "Update search", "Updates:SearchInterval", AgentSettingKind.Duration, "hours", 1, 24, "4"),
        new("inventory-interval", Intervals, "Inventory", "Inventory:CollectInterval", AgentSettingKind.Duration, "hours", 1, 24, "6"),
        new("max-backoff", Intervals, "Longest wait while the central server is unreachable", "CentralServer:MaxBackoff", AgentSettingKind.Duration, "minutes", 1, 60, "5"),
        new("log-level", Logging, "Log level", "Logging:File:MinimumLevel", AgentSettingKind.Choice, string.Empty, 0, 0, "Information", ["Warning", "Information", "Debug"]),
        new("log-files", Logging, "Log files kept", "Logging:File:RetainedFileCountLimit", AgentSettingKind.Number, string.Empty, 1, 365, "31"),
        new("memory-limit", Resources, "Memory limit (agent restarts above it)", "Resources:MaxPrivateMemoryMegabytes", AgentSettingKind.Number, "MB", 64, 4096, "200"),
        new("low-priority", Resources, "Below-normal process priority", "Resources:LowPriority", AgentSettingKind.Flag, string.Empty, 0, 0, "true"),
        new("min-free-disk", PreFlight, "Free space required on the system drive", "Updates:MinFreeDiskSpaceMegabytes", AgentSettingKind.Number, "MB", 512, 1_000_000, "4096"),
        new("include-drivers", Search, "Include drivers", "Updates:IncludeDrivers", AgentSettingKind.Flag, string.Empty, 0, 0, "true"),
        new("restart-warning", RestartNotice, "Warning before a restart", "Installation:RestartWarning", AgentSettingKind.Duration, "minutes", 1, 30, "5"),
        new("restart-message", RestartNotice, "Message to signed-in users", "Installation:RestartMessage", AgentSettingKind.Text, string.Empty, 0, 200, "SUSyphos restarts this server to complete Windows updates inside the maintenance window."),
    ];

    public static AgentSettingDefinition? Find(string? key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));

    /// <summary>Only known settings with valid values, normalized; everything else is dropped.</summary>
    public static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? values)
    {
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values ?? new Dictionary<string, string>())
        {
            if (Find(key) is { } definition && definition.Normalize(value) is { } normalized)
            {
                clean[definition.Key] = normalized;
            }
        }

        return clean;
    }
}

/// <summary>Where the value of an agent setting on the agent comes from.</summary>
public enum AgentSettingSource
{
    /// <summary>Nothing set: the agent's own default.</summary>
    AgentDefault = 0,

    /// <summary>The central server set it (system default, group or server).</summary>
    Central = 1,

    /// <summary>The server itself sets it (registry, group policy or its own configuration file), above the central value.</summary>
    Local = 2,
}

/// <summary>A setting as the agent applies it, reported with the heartbeat when it changed.</summary>
/// <param name="Value">The value in the form the dashboard shows (durations in the setting's unit).</param>
/// <param name="Source">Where it comes from.</param>
public sealed record AppliedAgentSetting(string Value, AgentSettingSource Source)
{
    /// <summary>Upper bound for a reported value.</summary>
    public const int MaxValueLength = 256;
}
