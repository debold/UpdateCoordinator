using Microsoft.Win32;

namespace UpdateCoordinator.Agent.Configuration;

/// <summary>
/// Registry locations of the agent settings (see docs/03-communication-protocol.md, section 3.3).
/// </summary>
internal static class AgentRegistry
{
    /// <summary>Written by the installer.</summary>
    public const string InstallerKeyPath = @"SOFTWARE\UpdateCoordinator\Agent";

    /// <summary>Written by group policy; overrides the installer values.</summary>
    public const string PolicyKeyPath = @"SOFTWARE\Policies\UpdateCoordinator\Agent";

    /// <summary>
    /// Adds the installer and policy keys as the configuration sources with the highest priority,
    /// so a group policy always wins over every other setting.
    /// </summary>
    public static IConfigurationBuilder AddAgentRegistry(this IConfigurationBuilder builder, RegistryKey root, string keyPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(new RegistryConfigurationSource(root, keyPrefix + InstallerKeyPath));
        builder.Add(new RegistryConfigurationSource(root, keyPrefix + PolicyKeyPath));
        return builder;
    }
}
