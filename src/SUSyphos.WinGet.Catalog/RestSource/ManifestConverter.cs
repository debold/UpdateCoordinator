using System.Globalization;
using System.Text.Json.Nodes;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SUSyphos.WinGet.Catalog.RestSource;

/// <summary>
/// Turns the YAML manifests of the WinGet community repository (version, installer and default locale manifest) into
/// the JSON objects of the WinGet REST source protocol. Fields are passed through by name, as both use the same names;
/// only what the REST protocol structures differently is changed.
/// </summary>
public static class ManifestConverter
{
    /// <summary>Fields of a manifest file that describe the file, not the package.</summary>
    private static readonly HashSet<string> FileFields = new(StringComparer.Ordinal) { "PackageIdentifier", "PackageVersion", "ManifestType", "ManifestVersion", "Installers", "DefaultLocale" };

    /// <summary>Fields whose values are numbers in the REST protocol (YAML gives every scalar as text).</summary>
    private static readonly HashSet<string> NumberFields = new(StringComparer.Ordinal) { "InstallerSuccessCodes", "InstallerReturnCode" };

    /// <summary>Architectures a Windows Server can run; installers for other ones are not cached.</summary>
    private static readonly HashSet<string> ServerArchitectures = new(StringComparer.OrdinalIgnoreCase) { "x64", "x86", "neutral" };

    /// <summary>Reads one YAML manifest into a JSON object.</summary>
    /// <exception cref="InvalidDataException">The text is no YAML mapping.</exception>
    public static JsonObject Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException($"The manifest is no valid YAML: {exception.Message}", exception);
        }

        return stream.Documents is [{ RootNode: YamlMappingNode root }]
            ? (JsonObject)Convert(root, null)!
            : throw new InvalidDataException("The manifest is not one YAML mapping.");
    }

    /// <summary>
    /// The installers of an installer manifest as the REST protocol lists them: fields given once at the top of the
    /// manifest are copied into every installer that does not set them itself (switches field by field). Servers run x64:
    /// when there is an x64 installer, only x64 and neutral ones are kept, otherwise x86 and neutral ones; others (ARM)
    /// are always left out. With <paramref name="keepX86"/> the x86 installers stay next to the x64 ones, for servers
    /// that have the 32-bit program installed and must keep it (an upgrade never changes the architecture).
    /// </summary>
    public static IReadOnlyList<JsonObject> Installers(JsonObject installerManifest, bool keepX86 = false)
    {
        ArgumentNullException.ThrowIfNull(installerManifest);
        if (installerManifest["Installers"] is not JsonArray installers)
        {
            throw new InvalidDataException("The installer manifest has no installers.");
        }

        var shared = installerManifest.Where(field => !FileFields.Contains(field.Key)).ToList();
        var result = new List<JsonObject>();
        foreach (var installer in installers.OfType<JsonObject>())
        {
            if (installer["Architecture"]?.GetValue<string>() is not { } architecture || !ServerArchitectures.Contains(architecture))
            {
                continue;
            }

            var merged = (JsonObject)installer.DeepClone();
            foreach (var (name, value) in shared)
            {
                if (merged[name] is null)
                {
                    merged[name] = value?.DeepClone();
                }
                else if (name == "InstallerSwitches" && merged[name] is JsonObject own && value is JsonObject common)
                {
                    foreach (var (switchName, switchValue) in common)
                    {
                        own[switchName] ??= switchValue?.DeepClone();
                    }
                }
            }

            result.Add(merged);
        }

        if (!keepX86 && result.Any(installer => Architecture(installer) == "x64"))
        {
            result.RemoveAll(installer => Architecture(installer) == "x86");
        }

        return result;
    }

    /// <summary>The default locale as the REST protocol carries it: the locale manifest without its file fields.</summary>
    public static JsonObject Locale(JsonObject localeManifest)
    {
        ArgumentNullException.ThrowIfNull(localeManifest);
        var locale = new JsonObject();
        foreach (var (name, value) in localeManifest)
        {
            if (!FileFields.Contains(name) || name == "PackageLocale")
            {
                locale[name] = value?.DeepClone();
            }
        }

        return locale;
    }

    /// <summary>True when the installers have one for x86 (to tell whether a cached version serves 32-bit programs).</summary>
    public static bool HasX86(IEnumerable<JsonObject> installers) => installers.Any(installer => Architecture(installer) == "x86");

    private static string? Architecture(JsonObject installer) => installer["Architecture"]?.GetValue<string>().ToLowerInvariant();

    private static JsonNode? Convert(YamlNode node, string? field) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(child =>
            KeyValuePair.Create(((YamlScalarNode)child.Key).Value ?? string.Empty, Convert(child.Value, ((YamlScalarNode)child.Key).Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(child => Convert(child, field)).ToArray()),
        YamlScalarNode scalar => Scalar(scalar, field),
        _ => null,
    };

    private static JsonValue? Scalar(YamlScalarNode scalar, string? field)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style is ScalarStyle.Plain)
        {
            if (text is "true" or "false")
            {
                return JsonValue.Create(text == "true");
            }

            if (field is not null && NumberFields.Contains(field) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                return JsonValue.Create(number);
            }

            if (text is "" or "~" or "null")
            {
                return null;
            }
        }

        return JsonValue.Create(text);
    }
}
