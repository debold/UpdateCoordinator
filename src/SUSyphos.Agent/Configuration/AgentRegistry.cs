using Microsoft.Win32;

using SUSyphos.Common.Configuration;

namespace SUSyphos.Agent.Configuration;

/// <summary>
/// Registry locations of the agent settings (see docs/03-communication-protocol.md, section 3.3).
/// </summary>
internal static class AgentRegistry
{
    /// <summary>Written by the installer.</summary>
    public const string InstallerKeyPath = @"SOFTWARE\SUSyphos\Agent";

    /// <summary>Written by group policy; overrides the installer values.</summary>
    public const string PolicyKeyPath = @"SOFTWARE\Policies\SUSyphos\Agent";

    /// <summary>Registry value name → configuration key. Only these values are read.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CentralServerUrl"] = "CentralServer:BaseAddress",
        ["TrustResetToken"] = "Trust:ResetToken",
        ["TrustAnchorFingerprint"] = "Trust:AnchorFingerprint",
    };

    /// <summary>The local reboot veto: only an explicit "1"/"true" allows reboots (docs/05-agent-design.md, reboots).</summary>
    internal static readonly IReadOnlyDictionary<string, string> SafetySwitches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AllowReboot"] = "Installation:AllowReboot",
    };

    /// <summary>
    /// Adds the installer and policy keys as the configuration sources with the highest priority,
    /// so a group policy always wins over every other setting.
    /// </summary>
    public static IConfigurationBuilder AddAgentRegistry(this IConfigurationBuilder builder, RegistryKey root, string keyPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(new RegistryConfigurationSource(root, keyPrefix + InstallerKeyPath, ValueMap, SafetySwitches));
        builder.Add(new RegistryConfigurationSource(root, keyPrefix + PolicyKeyPath, ValueMap, SafetySwitches));
        return builder;
    }
}
