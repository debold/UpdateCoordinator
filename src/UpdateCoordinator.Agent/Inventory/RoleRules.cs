using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.Inventory;

internal enum RoleConditionKind
{
    /// <summary>A registry key below HKLM exists.</summary>
    RegistryKeyExists,

    /// <summary>A registry value below HKLM exists; with <see cref="RoleCondition.EqualTo"/> it must also have that value (case-insensitive).</summary>
    RegistryValue,

    /// <summary>A Windows service with this name is installed.</summary>
    ServiceInstalled,
}

/// <summary>
/// One check of a role rule. Bound from configuration, so new rules need no code change.
/// </summary>
internal sealed class RoleCondition
{
    public RoleConditionKind Kind { get; set; }

    /// <summary>Registry path below HKLM (without hive), or the service name.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Registry value name (only for <see cref="RoleConditionKind.RegistryValue"/>).</summary>
    public string? ValueName { get; set; }

    /// <summary>Optional expected value (only for <see cref="RoleConditionKind.RegistryValue"/>).</summary>
    public string? EqualTo { get; set; }
}

/// <summary>
/// A server role and the conditions that detect it. The role is detected if any condition matches.
/// </summary>
internal sealed class RoleRule
{
    public string Role { get; set; } = string.Empty;

    public List<RoleCondition> AnyOf { get; set; } = [];
}

/// <summary>
/// Inventory settings. Bound from the "Inventory" configuration section.
/// </summary>
internal sealed partial class InventoryOptions : IValidatableObject
{
    public const string SectionName = "Inventory";

    /// <summary>How often the inventory is collected; it is only sent if it changed or <see cref="MaxSendInterval"/> passed.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "1.00:00:00")]
    public TimeSpan CollectInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The inventory is sent at least this often, even without changes.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
    public TimeSpan MaxSendInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Additional role rules. They extend the built-in rules (<see cref="DefaultRoleRules"/>);
    /// conditions for an existing role are added to its built-in conditions.
    /// </summary>
    public List<RoleRule> RoleRules { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxSendInterval < CollectInterval)
        {
            yield return new ValidationResult("MaxSendInterval must not be shorter than CollectInterval.", [nameof(MaxSendInterval)]);
        }

        foreach (var error in RoleRules.SelectMany(RoleRuleValidator.Validate))
        {
            yield return new ValidationResult(error, [nameof(RoleRules)]);
        }
    }
}

internal static partial class RoleRuleValidator
{
    private const int MaxPathLength = 512;

    public static IEnumerable<string> Validate(RoleRule rule)
    {
        if (!RoleNameRegex().IsMatch(rule.Role))
        {
            yield return $"Role name '{rule.Role}' is invalid (letters, digits, '.' and '-', starting with a letter).";
        }

        if (rule.AnyOf.Count == 0)
        {
            yield return $"Role '{rule.Role}' has no conditions.";
        }

        foreach (var condition in rule.AnyOf)
        {
            if (!Enum.IsDefined(condition.Kind))
            {
                yield return $"Role '{rule.Role}' has an unknown condition kind.";
            }

            if (string.IsNullOrWhiteSpace(condition.Path) || condition.Path.Length > MaxPathLength)
            {
                yield return $"Role '{rule.Role}' has a condition without a valid path.";
            }
            else if (condition.Path.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) || condition.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"Role '{rule.Role}': paths are relative to HKLM and must not contain the hive ('{condition.Path}').";
            }

            if (condition.Kind == RoleConditionKind.ServiceInstalled && condition.Path.Contains('\\', StringComparison.Ordinal))
            {
                yield return $"Role '{rule.Role}': a service name must not contain '\\'.";
            }

            if (condition.Kind == RoleConditionKind.RegistryValue && string.IsNullOrEmpty(condition.ValueName))
            {
                yield return $"Role '{rule.Role}': a registry value condition needs a ValueName.";
            }
        }
    }

    [GeneratedRegex(ValidationPatterns.RoleName)]
    private static partial Regex RoleNameRegex();
}
