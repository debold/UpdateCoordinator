using System.Text.Json;

using SUSyphos.Contracts.Agents;

namespace SUSyphos.Server.Assignment;

/// <summary>
/// Central agent settings of one level (system, group or server) stored as JSON, and the rule that combines the levels:
/// the server value wins, then the groups (several groups: the lowest number, else the group first by name), then the
/// system default; settings set nowhere keep the agent's own value.
/// </summary>
internal static class AgentSettingValues
{
    public static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return AgentSettings.Clean(JsonSerializer.Deserialize<Dictionary<string, string>>(json));
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Sorted JSON of the valid values, or null when none are set.</summary>
    public static string? ToJson(IReadOnlyDictionary<string, string> values)
    {
        var clean = AgentSettings.Clean(values);
        return clean.Count == 0 ? null : JsonSerializer.Serialize(new SortedDictionary<string, string>(clean, StringComparer.Ordinal));
    }

    /// <param name="system">System default.</param>
    /// <param name="groups">Values of the server's groups, ordered by group name.</param>
    /// <param name="server">Values set on the server itself.</param>
    public static Dictionary<string, string> Combine(IReadOnlyDictionary<string, string> system, IReadOnlyList<IReadOnlyDictionary<string, string>> groups, IReadOnlyDictionary<string, string> server)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(server);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in AgentSettings.All)
        {
            if (server.TryGetValue(definition.Key, out var own))
            {
                result[definition.Key] = own;
                continue;
            }

            var fromGroups = groups.Select(g => g.GetValueOrDefault(definition.Key)).OfType<string>().ToList();
            if (fromGroups.Count > 0)
            {
                result[definition.Key] = definition.Kind is AgentSettingKind.Duration or AgentSettingKind.Number
                    ? fromGroups.MinBy(v => int.Parse(v, System.Globalization.CultureInfo.InvariantCulture))!
                    : fromGroups[0];
                continue;
            }

            if (system.TryGetValue(definition.Key, out var fallback))
            {
                result[definition.Key] = fallback;
            }
        }

        return result;
    }

    /// <summary>
    /// The value of each setting and where it comes from ("this server", "group X", "system default", or the agent's
    /// own default), in the order of <see cref="AgentSettings.All"/>.
    /// </summary>
    public static IReadOnlyList<(AgentSettingDefinition Definition, string Value, string Source)> Explain(
        IReadOnlyDictionary<string, string> system, IReadOnlyList<(string Name, IReadOnlyDictionary<string, string> Values)> groups, IReadOnlyDictionary<string, string>? own, string ownLabel)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(groups);
        var ordered = groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var combined = Combine(system, [.. ordered.Select(g => g.Values)], own ?? new Dictionary<string, string>());
        return
        [
            .. AgentSettings.All.Select(definition =>
            {
                if (!combined.TryGetValue(definition.Key, out var value))
                {
                    return (definition, definition.AgentDefault, "agent default");
                }

                var source = own?.ContainsKey(definition.Key) == true ? ownLabel
                    : ordered.FirstOrDefault(g => g.Values.GetValueOrDefault(definition.Key) == value) is { Name: not null } group ? $"group {group.Name}"
                    : "system default";
                return (definition, value, source);
            }),
        ];
    }

    /// <summary>Reads the posted fields "AgentSetting.&lt;key&gt;" (and the pair AgentSettingNew / AgentSettingNewValue) of a level.</summary>
    public static Dictionary<string, string?> ReadForm(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var fields = form.ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var definition in AgentSettings.All)
        {
            if (fields.TryGetValue(FieldName(definition.Key), out var value))
            {
                values[definition.Key] = value;
            }
        }

        if (fields.GetValueOrDefault(NewKeyField) is { Length: > 0 } added)
        {
            values[added] = fields.GetValueOrDefault(NewValueField);
        }

        return values;
    }

    public const string NewKeyField = "AgentSettingNew";

    public const string NewValueField = "AgentSettingNewValue";

    public static string FieldName(string key) => "AgentSetting." + key;

    /// <summary>"6 hours", "on", "Debug": a value as the dashboard shows it.</summary>
    public static string Display(AgentSettingDefinition definition, string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Kind switch
        {
            AgentSettingKind.Flag => value == "true" ? "on" : "off",
            AgentSettingKind.Duration or AgentSettingKind.Number when definition.Unit.Length > 0 => $"{value} {definition.Unit}",
            _ => value,
        };
    }
}
