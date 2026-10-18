using Microsoft.Win32;

namespace UpdateCoordinator.Agent.Configuration;

/// <summary>
/// Reads agent settings from a registry key (written by the installer or by group policy)
/// and maps the registry value names to configuration keys.
/// </summary>
internal sealed class RegistryConfigurationSource(RegistryKey root, string subKeyPath) : IConfigurationSource
{
    public RegistryKey Root { get; } = root;

    public string SubKeyPath { get; } = subKeyPath;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new RegistryConfigurationProvider(this);
}

internal sealed class RegistryConfigurationProvider(RegistryConfigurationSource source) : ConfigurationProvider
{
    /// <summary>Registry value name → configuration key. Only these values are read.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CentralServerUrl"] = "CentralServer:BaseAddress",
        ["TrustResetToken"] = "Trust:ResetToken",
    };

    /// <summary>Upper bound for a single value; longer values are ignored.</summary>
    private const int MaxValueLength = 2048;

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var key = source.Root.OpenSubKey(source.SubKeyPath, writable: false);
        if (key is not null)
        {
            var present = new HashSet<string>(key.GetValueNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var (valueName, configurationKey) in ValueMap)
            {
                // Only plain strings are accepted; other value kinds are ignored rather than converted.
                // GetValueKind throws for missing values, so presence is checked first.
                if (present.Contains(valueName)
                    && key.GetValueKind(valueName) is RegistryValueKind.String
                    && key.GetValue(valueName) is string value
                    && !string.IsNullOrWhiteSpace(value)
                    && value.Length <= MaxValueLength)
                {
                    data[configurationKey] = value.Trim();
                }
            }

            ReadRebootVeto(key, present, data);
        }

        Data = data;
    }

    /// <summary>
    /// AllowReboot is a safety veto: as string or DWORD, only "1"/"true" (or 1) allows reboots; any other present value
    /// forbids them, so a typo never enables a reboot.
    /// </summary>
    private static void ReadRebootVeto(RegistryKey key, HashSet<string> present, Dictionary<string, string?> data)
    {
        if (!present.Contains(AllowRebootValueName))
        {
            return;
        }

        var allowed = key.GetValue(AllowRebootValueName) switch
        {
            int number => number == 1,
            string text => text.Trim() is "1" || string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        data["Installation:AllowReboot"] = allowed ? "true" : "false";
    }

    internal const string AllowRebootValueName = "AllowReboot";
}
