using Microsoft.Win32;

using UpdateCoordinator.Common.Configuration;

namespace UpdateCoordinator.Server;

/// <summary>
/// Settings the installer writes to HKLM\SOFTWARE\UpdateCoordinator\Server (docs/admin/README.md, installation).
/// They take precedence over appsettings.json, so the installer options survive upgrades of the installed files.
/// </summary>
internal static class ServerRegistry
{
    public const string KeyPath = @"SOFTWARE\UpdateCoordinator\Server";

    /// <summary>Registry value name → configuration key. Only these values are read.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CertificateThumbprint"] = "Trust:CertificateThumbprint",
        ["HttpsUrl"] = "Kestrel:Endpoints:Https:Url",
    };

    /// <summary>Inserts the installer settings after appsettings.json and before environment variables and the command line.</summary>
    public static IConfigurationBuilder AddServerRegistry(this IConfigurationBuilder builder, RegistryKey root, string keyPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(builder);
        // After the JSON files and before environment variables and the command line, which stay the strongest.
        var index = builder.Sources.ToList().FindLastIndex(s => s is FileConfigurationSource or StreamConfigurationSource) + 1;
        builder.Sources.Insert(index, new RegistryConfigurationSource(root, keyPrefix + KeyPath, ValueMap));
        return builder;
    }
}
