using System.Text.Json.Nodes;

namespace SUSyphos.WinGet.Catalog.RestSource;

/// <summary>An answer of the REST source: HTTP status and JSON body (null for 204 and 404).</summary>
public sealed record RestAnswer(int Status, JsonObject? Body);

/// <summary>
/// The WinGet REST source protocol (schema 1.1.0) over a <see cref="PackageCache"/>: <c>information</c>,
/// <c>manifestSearch</c> and <c>packageManifests</c>. Installers are offered from the cache under
/// <c>{filesBase}/{file}</c>. Transport (HTTPS, routing) belongs to the host.
/// </summary>
public sealed class RestSourceResponder(PackageCache cache, Uri filesBase, string sourceIdentifier = "susyphos")
{
    public const string SupportedVersion = "1.1.0";

    /// <summary>Fields a search may match on; others are reported back as unsupported.</summary>
    private static readonly string[] SupportedFields = ["PackageIdentifier", "PackageName", "Moniker", "Command", "Tag", "ProductCode", "UpgradeCode", "PackageFamilyName", "NormalizedPackageNameAndPublisher"];

    public RestAnswer Information() => new(200, new JsonObject
    {
        ["Data"] = new JsonObject
        {
            ["SourceIdentifier"] = sourceIdentifier,
            ["ServerSupportedVersions"] = new JsonArray(SupportedVersion),
        },
    });

    /// <summary>Answers a manifestSearch request; 204 when nothing matches.</summary>
    public RestAnswer Search(JsonObject? request)
    {
        var query = request?["Query"] as JsonObject;
        var inclusions = (request?["Inclusions"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var filters = (request?["Filters"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var unsupported = inclusions.Concat(filters)
            .Select(condition => condition["PackageMatchField"]?.GetValue<string>())
            .OfType<string>()
            .Where(field => !SupportedFields.Contains(field, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var limit = request?["MaximumResults"]?.GetValue<int>() is > 0 and var maximum ? maximum : int.MaxValue;

        var packages = cache.Versions
            .GroupBy(version => version.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
            .Where(package =>
                (query is null || Matches(package, null, query))
                && filters.All(filter => Matches(package, filter["PackageMatchField"]?.GetValue<string>(), filter["RequestMatch"] as JsonObject))
                && (inclusions.Count == 0 || inclusions.Any(inclusion => Matches(package, inclusion["PackageMatchField"]?.GetValue<string>(), inclusion["RequestMatch"] as JsonObject))))
            .Take(limit)
            .Select(package =>
            {
                var latest = package.MaxBy(version => version.PackageVersion, VersionComparer.Instance)!;
                return (JsonNode)new JsonObject
                {
                    ["PackageIdentifier"] = latest.PackageIdentifier,
                    ["PackageName"] = latest.DefaultLocale["PackageName"]?.DeepClone(),
                    ["Publisher"] = latest.DefaultLocale["Publisher"]?.DeepClone(),
                    ["Versions"] = new JsonArray(package.OrderByDescending(version => version.PackageVersion, VersionComparer.Instance).Select(version => (JsonNode)new JsonObject
                    {
                        ["PackageVersion"] = version.PackageVersion,
                        ["ProductCodes"] = Strings(Values(version, "ProductCode")),
                        ["UpgradeCodes"] = Strings(Values(version, "UpgradeCode")),
                        ["PackageFamilyNames"] = Strings(Values(version, "PackageFamilyName")),
                    }).ToArray()),
                };
            })
            .ToArray();

        return packages.Length == 0
            ? new RestAnswer(204, null)
            : new RestAnswer(200, new JsonObject
            {
                ["Data"] = new JsonArray(packages),
                ["UnsupportedPackageMatchFields"] = Strings(unsupported),
                ["RequiredPackageMatchFields"] = new JsonArray(),
            });
    }

    /// <summary>All cached versions of a package (or one, with <paramref name="version"/>); 404 when there is none.</summary>
    public RestAnswer Manifest(string id, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        var versions = cache.Versions
            .Where(entry => string.Equals(entry.PackageIdentifier, id, StringComparison.OrdinalIgnoreCase)
                && (version is null || string.Equals(entry.PackageVersion, version, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.PackageVersion, VersionComparer.Instance)
            .ToList();
        if (versions.Count == 0)
        {
            return new RestAnswer(404, null);
        }

        return new RestAnswer(200, new JsonObject
        {
            ["Data"] = new JsonObject
            {
                ["PackageIdentifier"] = versions[0].PackageIdentifier,
                ["Versions"] = new JsonArray(versions.Select(entry => (JsonNode)new JsonObject
                {
                    ["PackageVersion"] = entry.PackageVersion,
                    ["DefaultLocale"] = entry.DefaultLocale.DeepClone(),
                    ["Installers"] = new JsonArray(entry.Installers.Select(Served).ToArray()),
                    ["Locales"] = new JsonArray(),
                }).ToArray()),
            },
        });
    }

    /// <summary>An installer as served: pointing at the cache instead of its original address.</summary>
    private JsonNode Served(JsonObject installer)
    {
        var served = (JsonObject)installer.DeepClone();
        var file = served["CachedFile"]!.GetValue<string>();
        served.Remove("CachedFile");
        served["InstallerUrl"] = new Uri(filesBase, file).ToString();
        return served;
    }

    private static bool Matches(IGrouping<string, CachedVersion> package, string? field, JsonObject? match)
    {
        var keyword = match?["KeyWord"]?.GetValue<string>();
        if (keyword is null)
        {
            return false;
        }

        var type = match?["MatchType"]?.GetValue<string>() ?? "Substring";
        var candidates = field is null
            ? package.SelectMany(version => Candidates(version, "PackageIdentifier").Concat(Candidates(version, "PackageName")).Concat(Candidates(version, "Moniker")).Concat(Candidates(version, "Tag")))
            : package.SelectMany(version => Candidates(version, field));
        return candidates.Any(candidate => type switch
        {
            "Exact" => string.Equals(candidate, keyword, StringComparison.Ordinal),
            "CaseInsensitive" => string.Equals(candidate, keyword, StringComparison.OrdinalIgnoreCase),
            "StartsWith" => candidate.StartsWith(keyword, StringComparison.OrdinalIgnoreCase),
            _ => candidate.Contains(keyword, StringComparison.OrdinalIgnoreCase),
        });
    }

    private static IEnumerable<string> Candidates(CachedVersion version, string field) => field switch
    {
        "PackageIdentifier" => [version.PackageIdentifier],
        "PackageName" or "Moniker" => Text(version.DefaultLocale[field]),
        "Tag" => Text(version.DefaultLocale["Tags"]),
        "Command" => version.Installers.SelectMany(installer => Text(installer["Commands"])),
        "ProductCode" or "UpgradeCode" or "PackageFamilyName" => Values(version, field),
        "NormalizedPackageNameAndPublisher" => [Normalize(Text(version.DefaultLocale["PackageName"]).FirstOrDefault() + Text(version.DefaultLocale["Publisher"]).FirstOrDefault())],
        _ => [],
    };

    /// <summary>Values of a field on the installers and their AppsAndFeaturesEntries.</summary>
    private static IEnumerable<string> Values(CachedVersion version, string field) =>
        version.Installers
            .SelectMany(installer => Text(installer[field]).Concat((installer["AppsAndFeaturesEntries"] as JsonArray)?.OfType<JsonObject>().SelectMany(entry => Text(entry[field])) ?? []))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Text(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(item => item?.GetValue<string>()).OfType<string>(),
        JsonValue value when value.TryGetValue<string>(out var text) => [text],
        _ => [],
    };

    private static string Normalize(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode)JsonValue.Create(value)).ToArray());
}

/// <summary>Orders package versions by their numeric parts (1.10 after 1.9), the rest as text.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var left = (x ?? string.Empty).Split('.', '-', '+');
        var right = (y ?? string.Empty).Split('.', '-', '+');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : "0";
            var b = i < right.Length ? right[i] : "0";
            var result = long.TryParse(a, out var na) && long.TryParse(b, out var nb) ? na.CompareTo(nb) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }
}
