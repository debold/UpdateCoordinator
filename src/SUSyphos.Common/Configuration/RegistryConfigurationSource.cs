using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace SUSyphos.Common.Configuration;

/// <summary>
/// Reads settings from a registry key (written by an installer or by group policy) and maps registry value names to
/// configuration keys. Only the listed values are read; everything else in the key is ignored.
/// </summary>
/// <param name="root">Registry hive, normally HKEY_LOCAL_MACHINE.</param>
/// <param name="subKeyPath">Key below the hive.</param>
/// <param name="values">String values: registry value name → configuration key.</param>
/// <param name="safetySwitches">
/// Switches that allow something risky: registry value name → configuration key. As string or DWORD only "1"/"true" (or 1)
/// means true; any other present value means false, so a typo never enables what the switch guards.
/// </param>
public sealed class RegistryConfigurationSource(
    RegistryKey root,
    string subKeyPath,
    IReadOnlyDictionary<string, string> values,
    IReadOnlyDictionary<string, string>? safetySwitches = null) : IConfigurationSource
{
    public RegistryKey Root { get; } = root;

    public string SubKeyPath { get; } = subKeyPath;

    public IReadOnlyDictionary<string, string> Values { get; } = values;

    public IReadOnlyDictionary<string, string> SafetySwitches { get; } = safetySwitches ?? new Dictionary<string, string>();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new RegistryConfigurationProvider(this);
}

public sealed class RegistryConfigurationProvider(RegistryConfigurationSource source) : ConfigurationProvider
{
    /// <summary>Upper bound for a single value; longer values are ignored.</summary>
    private const int MaxValueLength = 2048;

    public override void Load()
    {
        ArgumentNullException.ThrowIfNull(source);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var key = source.Root.OpenSubKey(source.SubKeyPath, writable: false);
        if (key is not null)
        {
            var present = new HashSet<string>(key.GetValueNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var (valueName, configurationKey) in source.Values)
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

            foreach (var (valueName, configurationKey) in source.SafetySwitches.Where(s => present.Contains(s.Key)))
            {
                var enabled = key.GetValue(valueName) switch
                {
                    int number => number == 1,
                    string text => text.Trim() is "1" || string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
                data[configurationKey] = enabled ? "true" : "false";
            }
        }

        Data = data;
    }
}
