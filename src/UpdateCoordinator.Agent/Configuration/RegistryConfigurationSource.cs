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
        }

        Data = data;
    }
}
