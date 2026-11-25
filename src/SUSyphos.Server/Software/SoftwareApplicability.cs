using System.Text.Json.Nodes;

namespace SUSyphos.Server.Software;

/// <summary>
/// Whether a package version can be installed on a server at all, read from its manifest before it is released there.
/// WinGet itself refuses these cases on the server with "no applicable installer" (0x8A150010); the central server knows
/// the Windows version of every server and says why instead (Marc, 2026-11-21):
/// <list type="bullet">
/// <item>a <c>MinimumOSVersion</c> above the server's Windows version,</item>
/// <item>portable packages (also inside a ZIP) below Windows 10.0.18362, which WinGet does not allow there (its
/// "Portable Install" filter; Windows Server 2016 and 2019),</item>
/// <item><c>ElevationRequirement: elevationProhibited</c>: the installer refuses administrator rights, and the agent
/// installs as SYSTEM,</item>
/// <item>Microsoft Store installers, which the agent does not install as SYSTEM,</item>
/// <item>MSIX installers below Windows 10.0.19041: the agent installs them for all users of the machine (provisioned)
/// and finds them again only with the provisioned list of Windows, which exists from 10.0.19041 on (Windows Server 2022),</item>
/// <item>installers for one user only (<c>Scope: user</c>): the agent installs for the machine.</item>
/// </list>
/// A version fits when one of its installers fits; the reason names the first installer's problem.
/// </summary>
public static class SoftwareApplicability
{
    /// <summary>First Windows build that allows portable packages (WinGet: unvirtualized resources, 10.0.18362).</summary>
    public static readonly Version PortableMinimum = new(10, 0, 18362);

    /// <summary>First Windows build with the provisioned list of packaged apps (PackageManager.FindProvisionedPackages).</summary>
    public static readonly Version PackagedAppMinimum = new(10, 0, 19041);

    /// <summary>
    /// Null when the version can be installed on a server with <paramref name="osVersion"/>, else why not (one sentence).
    /// An unreadable manifest or Windows version counts as installable: WinGet decides on the server as before.
    /// </summary>
    public static string? WhyNot(string? manifestJson, string? osVersion)
    {
        if (string.IsNullOrEmpty(manifestJson) || !TryVersion(osVersion, out var windows))
        {
            return null;
        }

        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(manifestJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        string? first = null;
        foreach (var installer in (manifest?["Installers"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var reason = WhyNot(installer, manifest!, windows);
            if (reason is null)
            {
                return null;
            }

            first ??= reason;
        }

        return first;
    }

    private static string? WhyNot(JsonObject installer, JsonObject manifest, Version windows)
    {
        var type = Text(installer, "InstallerType");
        var effective = type is "zip" ? Text(installer, "NestedInstallerType") ?? type : type;
        if (Text(installer, "MinimumOSVersion") is { } minimum && TryVersion(minimum, out var needed) && windows < needed)
        {
            return $"Needs Windows {minimum} or later; this server runs {Short(windows)}.";
        }

        if (effective == "portable" && windows < PortableMinimum)
        {
            return $"WinGet installs portable packages only from Windows {PortableMinimum} on (Windows Server 2022); this server runs {Short(windows)}.";
        }

        if (Text(installer, "ElevationRequirement") == "elevationprohibited")
        {
            return "The installer refuses administrator rights, and the agent installs as SYSTEM.";
        }

        if (effective is "msstore")
        {
            return "The package comes only from the Microsoft Store, which the agent does not install from as SYSTEM.";
        }

        if (effective is "msix" or "appx" && windows < PackagedAppMinimum)
        {
            return $"SUSyphos installs MSIX packages for all users from Windows {Short(PackagedAppMinimum)} on (Windows Server 2022); this server runs {Short(windows)}.";
        }

        if ((Text(installer, "Scope") ?? Text(manifest, "Scope")) is "user")
        {
            return "The installer installs only for one user, and the agent installs for the machine.";
        }

        return null;
    }

    private static string? Text(JsonObject installer, string field) =>
        installer[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text.ToLowerInvariant() : null;

    private static string Short(Version version) => version.Build >= 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : version.ToString();

    private static bool TryVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text.Trim(), out var parsed))
        {
            return false;
        }

        // Only major.minor.build count; a revision (patch level of a build) never decides.
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }
}
