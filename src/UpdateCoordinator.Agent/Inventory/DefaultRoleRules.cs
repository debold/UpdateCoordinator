namespace UpdateCoordinator.Agent.Inventory;

/// <summary>
/// Built-in role rules shipped with the agent (see docs/05-agent-design.md, section 2).
/// Further rules can be added through configuration (Inventory:RoleRules) without code changes.
/// </summary>
internal static class DefaultRoleRules
{
    public static IReadOnlyList<RoleRule> All { get; } =
    [
        Rule("DomainController",
            // ProductType "LanmanNt" is set only on domain controllers; local and needs no LDAP query.
            new RoleCondition { Kind = RoleConditionKind.RegistryValue, Path = @"SYSTEM\CurrentControlSet\Control\ProductOptions", ValueName = "ProductType", EqualTo = "LanmanNt" }),
        Rule("Exchange",
            new RoleCondition { Kind = RoleConditionKind.RegistryKeyExists, Path = @"SOFTWARE\Microsoft\ExchangeServer\v15\Setup" },
            new RoleCondition { Kind = RoleConditionKind.ServiceInstalled, Path = "MSExchangeIS" }),
        Rule("SqlServer",
            new RoleCondition { Kind = RoleConditionKind.RegistryKeyExists, Path = @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL" }),
        Rule("ClusterNode",
            new RoleCondition { Kind = RoleConditionKind.ServiceInstalled, Path = "ClusSvc" },
            new RoleCondition { Kind = RoleConditionKind.RegistryKeyExists, Path = "Cluster" }),
        Rule("HyperVHost",
            new RoleCondition { Kind = RoleConditionKind.ServiceInstalled, Path = "vmms" }),
    ];

    private static RoleRule Rule(string role, params RoleCondition[] conditions) => new() { Role = role, AnyOf = [.. conditions] };
}
