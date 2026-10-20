using Microsoft.Extensions.Options;

namespace SUSyphos.Agent.Inventory;

/// <summary>
/// Evaluates the built-in and configured role rules against the local system.
/// </summary>
internal sealed class RoleDetector(ISystemProbe probe, IOptions<InventoryOptions> options)
{
    /// <summary>All rules in effect: built-in rules extended by configured ones (same role name = additional conditions).</summary>
    public IReadOnlyList<RoleRule> Rules { get; } = Merge(DefaultRoleRules.All, options.Value.RoleRules);

    public IReadOnlyList<string> Detect() =>
        [.. Rules.Where(rule => rule.AnyOf.Any(Matches)).Select(rule => rule.Role).Order(StringComparer.Ordinal)];

    private bool Matches(RoleCondition condition) => condition.Kind switch
    {
        RoleConditionKind.RegistryKeyExists => probe.RegistryKeyExists(condition.Path),
        RoleConditionKind.ServiceInstalled => probe.ServiceInstalled(condition.Path),
        RoleConditionKind.RegistryValue => probe.GetRegistryValue(condition.Path, condition.ValueName!) is { } value
            && (condition.EqualTo is null || string.Equals(value, condition.EqualTo, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static List<RoleRule> Merge(IReadOnlyList<RoleRule> builtIn, IReadOnlyList<RoleRule> configured)
    {
        var merged = builtIn.ToDictionary(
            rule => rule.Role,
            rule => new RoleRule { Role = rule.Role, AnyOf = [.. rule.AnyOf] },
            StringComparer.OrdinalIgnoreCase);

        foreach (var rule in configured)
        {
            if (merged.TryGetValue(rule.Role, out var existing))
            {
                existing.AnyOf.AddRange(rule.AnyOf);
            }
            else
            {
                merged[rule.Role] = new RoleRule { Role = rule.Role, AnyOf = [.. rule.AnyOf] };
            }
        }

        return [.. merged.Values];
    }
}
