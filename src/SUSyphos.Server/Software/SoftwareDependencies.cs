using System.Text.Json;
using System.Text.Json.Nodes;

namespace SUSyphos.Server.Software;

/// <summary>A package another package needs (manifest field <c>Dependencies.PackageDependencies</c>).</summary>
/// <param name="PackageId">WinGet id of the dependency.</param>
/// <param name="MinimumVersion">Lowest version it needs; null for any.</param>
public sealed record SoftwareDependency(string PackageId, string? MinimumVersion);

/// <summary>
/// Package dependencies of a version. WinGet installs them before the package, from the same source; when our source does
/// not offer them, the installation fails (manifest audit, 2026-11-21: the ODBC driver 17 and the OLE DB driver 18 need
/// Microsoft.VCRedist.2015+.x86). The catalog therefore adds the packages a version depends on, and the installer cache
/// offers their newest version together with the version that needs them.
/// </summary>
public static class SoftwareDependencies
{
    /// <summary>The package dependencies of all installers of a stored manifest (each package once, highest minimum).</summary>
    public static IReadOnlyList<SoftwareDependency> Of(string? manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson))
        {
            return [];
        }

        try
        {
            return Of(((JsonNode.Parse(manifestJson) as JsonObject)?["Installers"] as JsonArray)?.OfType<JsonObject>() ?? []);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The package dependencies of installers as the REST source serves them.</summary>
    public static IReadOnlyList<SoftwareDependency> Of(IEnumerable<JsonObject> installers) =>
        [.. installers
            .SelectMany(installer => ((installer["Dependencies"] as JsonObject)?["PackageDependencies"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(dependency => (Id: Text(dependency["PackageIdentifier"]), Minimum: Text(dependency["MinimumVersion"])))
            .Where(dependency => SoftwarePackages.IsValidId(dependency.Id))
            .GroupBy(dependency => dependency.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SoftwareDependency(group.First().Id!, group.Select(d => d.Minimum).OfType<string>().Max(SUSyphos.WinGet.Catalog.RestSource.VersionComparer.Instance)))
            .OrderBy(dependency => dependency.PackageId, StringComparer.OrdinalIgnoreCase)];

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
