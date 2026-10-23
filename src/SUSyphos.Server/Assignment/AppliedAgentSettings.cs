using System.Text.Json;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Server.Assignment;

/// <summary>
/// The agent settings an agent reports as applied (heartbeat), stored per server: what the dashboard shows next to the
/// central value and what goes to the activity list when it changes. The report is agent input and is checked here.
/// </summary>
internal static class AppliedAgentSettings
{
    /// <summary>Only known settings with a printable value of bounded length and a defined source; null if nothing is left.</summary>
    public static Dictionary<string, AppliedAgentSetting>? Clean(IReadOnlyDictionary<string, AppliedAgentSetting>? reported)
    {
        if (reported is null || reported.Count > AgentSettings.All.Count * 2)
        {
            return null;
        }

        var clean = new Dictionary<string, AppliedAgentSetting>(StringComparer.Ordinal);
        foreach (var (key, setting) in reported)
        {
            if (AgentSettings.Find(key) is { } definition
                && setting is { Value: { Length: > 0 and <= AppliedAgentSetting.MaxValueLength } value }
                && !value.Any(char.IsControl)
                && Enum.IsDefined(setting.Source))
            {
                clean[definition.Key] = setting;
            }
        }

        return clean.Count == 0 ? null : clean;
    }

    public static Dictionary<string, AppliedAgentSetting> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, AppliedAgentSetting>(StringComparer.Ordinal);
        }

        try
        {
            return Clean(JsonSerializer.Deserialize<Dictionary<string, AppliedAgentSetting>>(json)) ?? new Dictionary<string, AppliedAgentSetting>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, AppliedAgentSetting>(StringComparer.Ordinal);
        }
    }

    public static string ToJson(IReadOnlyDictionary<string, AppliedAgentSetting> settings) =>
        JsonSerializer.Serialize(new SortedDictionary<string, AppliedAgentSetting>(settings.ToDictionary(), StringComparer.Ordinal));

    /// <summary>
    /// Text for the activity list: the settings that changed since the last report, or on the first report those the
    /// central server sets; null if nothing worth a line changed.
    /// </summary>
    public static string? DescribeChange(IReadOnlyDictionary<string, AppliedAgentSetting> previous, IReadOnlyDictionary<string, AppliedAgentSetting> current)
    {
        var changed = AgentSettings.All
            .Where(d => current.ContainsKey(d.Key))
            .Where(d => previous.Count == 0 ? current[d.Key].Source == AgentSettingSource.Central : previous.GetValueOrDefault(d.Key) != current[d.Key])
            .Select(d => $"{d.Label} {AgentSettingValues.Display(d, current[d.Key].Value)} ({SourceText(current[d.Key].Source)})")
            .ToList();
        if (changed.Count == 0)
        {
            return null;
        }

        var restart = AgentSettings.All.Any(d => d.NeedsRestart && current.ContainsKey(d.Key) && previous.ContainsKey(d.Key) && previous[d.Key].Value != current[d.Key].Value)
            ? " The agent service restarted to apply them."
            : string.Empty;
        var text = "Agent applies new settings: " + string.Join("; ", changed) + "." + restart;
        return text.Length <= 1000 ? text : text[..999] + "…";
    }

    public static string SourceText(AgentSettingSource source) => source switch
    {
        AgentSettingSource.Central => "central",
        AgentSettingSource.Local => "set on the server",
        _ => "agent default",
    };
}
