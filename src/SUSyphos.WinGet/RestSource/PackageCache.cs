using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SUSyphos.WinGet.RestSource;

/// <summary>One package version in the cache: its default locale and its installers, which point at cached files.</summary>
public sealed record CachedVersion(string PackageIdentifier, string PackageVersion, JsonObject DefaultLocale, IReadOnlyList<JsonObject> Installers);

/// <summary>
/// The installer cache of a private WinGet source: <c>index.json</c> with the cached package versions and the folder
/// <c>files</c> with the installers, each named after its SHA-256. Every file is checked against its hash when the cache
/// is opened, so nothing is served that differs from what the manifest promised.
/// </summary>
public sealed partial class PackageCache
{
    public const string IndexFileName = "index.json";
    public const string FilesFolderName = "files";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly List<CachedVersion> _versions;

    private PackageCache(string folder, List<CachedVersion> versions)
    {
        Folder = folder;
        _versions = versions;
    }

    public string Folder { get; }

    public IReadOnlyList<CachedVersion> Versions => _versions;

    public string FilesFolder => Path.Combine(Folder, FilesFolderName);

    /// <summary>Opens a cache folder (an empty one when it does not exist yet).</summary>
    /// <param name="folder">Folder of the cache.</param>
    /// <param name="problems">Collects entries that were dropped and why.</param>
    /// <param name="verifyFiles">Checks the SHA-256 of every cached file (for serving).</param>
    public static PackageCache Open(string folder, ICollection<string> problems, bool verifyFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(problems);
        var full = Path.GetFullPath(folder);
        var index = Path.Combine(full, IndexFileName);
        var versions = new List<CachedVersion>();
        if (File.Exists(index))
        {
            var root = JsonNode.Parse(File.ReadAllText(index)) as JsonArray ?? throw new InvalidDataException($"{index} is no JSON array.");
            foreach (var entry in root.OfType<JsonObject>())
            {
                if (Read(entry, full, verifyFiles, out var problem) is { } version)
                {
                    versions.Add(version);
                }
                else
                {
                    problems.Add(problem!);
                }
            }
        }

        return new PackageCache(full, versions);
    }

    /// <summary>The name a cached installer gets: its SHA-256 and the extension of its download address.</summary>
    public static string FileName(string sha256, Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Sha256Text().IsMatch(sha256))
        {
            throw new ArgumentException($"'{sha256}' is no SHA-256.", nameof(sha256));
        }

        var extension = Path.GetExtension(source.AbsolutePath).ToLowerInvariant();
        return sha256.ToLowerInvariant() + (Extension().IsMatch(extension) ? extension : ".bin");
    }

    /// <summary>True for names <see cref="FileName"/> produces; anything else is never served.</summary>
    public static bool IsCacheFileName(string name) => CacheFile().IsMatch(name);

    /// <summary>SHA-256 of a file, as lower-case hex.</summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Adds or replaces a package version and writes the index.</summary>
    public void Put(CachedVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        _versions.RemoveAll(existing => Same(existing, version.PackageIdentifier, version.PackageVersion));
        _versions.Add(version);
        Directory.CreateDirectory(Folder);
        var index = new JsonArray(_versions.Select(entry => (JsonNode)new JsonObject
        {
            ["PackageIdentifier"] = entry.PackageIdentifier,
            ["PackageVersion"] = entry.PackageVersion,
            ["DefaultLocale"] = entry.DefaultLocale.DeepClone(),
            ["Installers"] = new JsonArray(entry.Installers.Select(installer => (JsonNode)installer.DeepClone()).ToArray()),
        }).ToArray());
        File.WriteAllText(Path.Combine(Folder, IndexFileName), index.ToJsonString(Indented));
    }

    private static bool Same(CachedVersion version, string id, string packageVersion) =>
        string.Equals(version.PackageIdentifier, id, StringComparison.OrdinalIgnoreCase) && string.Equals(version.PackageVersion, packageVersion, StringComparison.OrdinalIgnoreCase);

    private static CachedVersion? Read(JsonObject entry, string folder, bool verifyFiles, out string? problem)
    {
        problem = null;
        var id = entry["PackageIdentifier"]?.GetValue<string>();
        var version = entry["PackageVersion"]?.GetValue<string>();
        if (id is null || version is null || entry["DefaultLocale"] is not JsonObject locale || entry["Installers"] is not JsonArray installers)
        {
            problem = "An index entry lacks identifier, version, locale or installers.";
            return null;
        }

        var list = new List<JsonObject>();
        foreach (var installer in installers.OfType<JsonObject>())
        {
            var file = installer["CachedFile"]?.GetValue<string>();
            var sha256 = installer["InstallerSha256"]?.GetValue<string>();
            if (file is null || !IsCacheFileName(file) || sha256 is null || !file.StartsWith(sha256, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"{id} {version}: an installer has no valid cached file.";
                return null;
            }

            var path = Path.Combine(folder, FilesFolderName, file);
            if (!File.Exists(path) || verifyFiles && !string.Equals(Hash(path), sha256, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"{id} {version}: {file} is missing or does not match its SHA-256.";
                return null;
            }

            list.Add((JsonObject)installer.DeepClone());
        }

        return new CachedVersion(id, version, (JsonObject)locale.DeepClone(), list);
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex Sha256Text();

    [GeneratedRegex(@"^\.[a-z0-9]{1,8}$")]
    private static partial Regex Extension();

    [GeneratedRegex(@"^[0-9a-f]{64}\.[a-z0-9]{1,8}$")]
    private static partial Regex CacheFile();
}
