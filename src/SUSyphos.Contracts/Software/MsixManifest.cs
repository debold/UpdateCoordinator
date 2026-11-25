using System.Xml;
using System.Xml.Linq;

namespace SUSyphos.Contracts.Software;

/// <summary>A package an MSIX bundle lists.</summary>
/// <param name="Version">Identity version of the package.</param>
/// <param name="Architecture">Processor architecture (x64, x86, arm64, neutral), null when not given.</param>
/// <param name="IsApplication">True for an application package, false for a resource package.</param>
public sealed record MsixBundlePackage(string Version, string? Architecture, bool IsApplication);

/// <summary>The identity version of an MSIX bundle or package and, for a bundle, the packages it lists.</summary>
public sealed record MsixIdentity(string Version, IReadOnlyList<MsixBundlePackage> Packages)
{
    /// <summary>
    /// The version of the application package for this processor architecture: the package Windows installs for the
    /// users, whose version WinGet and <c>Get-AppxPackage</c> show; a neutral one when there is none for the architecture.
    /// Null for a single package or a bundle without a fitting application package.
    /// </summary>
    public string? ApplicationVersion(string architecture) =>
        Packages.FirstOrDefault(p => p.IsApplication && string.Equals(p.Architecture, architecture, StringComparison.OrdinalIgnoreCase))?.Version
        ?? Packages.FirstOrDefault(p => p.IsApplication && string.Equals(p.Architecture, "neutral", StringComparison.OrdinalIgnoreCase))?.Version;
}

/// <summary>
/// Reads the manifest of an MSIX bundle (<c>AppxMetadata/AppxBundleManifest.xml</c>) or package (<c>AppxManifest.xml</c>).
/// A bundle carries its own version, which can differ from the versions of its packages (lab, 2026-11-25: PowerShell 7.7
/// is provisioned as the bundle 2026.1110.418.0, its x64 package is 7.7.0.0). Shared by the agent (installed bundles) and
/// the central server (cached installers).
/// </summary>
public static class MsixManifest
{
    /// <summary>Path of the bundle manifest inside a bundle or the folder of a provisioned bundle.</summary>
    public const string BundleManifestPath = "AppxMetadata/AppxBundleManifest.xml";

    /// <summary>Path of the manifest inside a package.</summary>
    public const string PackageManifestPath = "AppxManifest.xml";

    /// <summary>Largest manifest read.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// The identity of the manifest, null when it is no readable bundle or package manifest. No DTD is processed and no
    /// external resource is loaded; versions that are not four-part numbers are left out.
    /// </summary>
    public static MsixIdentity? Parse(Stream manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try
        {
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            var root = XDocument.Load(reader).Root;
            if (root is null || root.Name.LocalName is not ("Bundle" or "Package"))
            {
                return null;
            }

            var version = Valid((string?)root.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Version"));
            if (version is null)
            {
                return null;
            }

            var packages = root.Name.LocalName != "Bundle" ? [] : root.Elements()
                .Where(e => e.Name.LocalName == "Packages")
                .Elements()
                .Where(e => e.Name.LocalName == "Package")
                .Select(e => (Version: Valid((string?)e.Attribute("Version")), Architecture: (string?)e.Attribute("Architecture"), Type: (string?)e.Attribute("Type")))
                .Where(p => p.Version is not null)
                .Select(p => new MsixBundlePackage(p.Version!, p.Architecture, !string.Equals(p.Type, "resource", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return new MsixIdentity(version, packages);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>The architecture name the manifests use for this machine (x64, x86, arm64).</summary>
    public static string MachineArchitecture() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "x64",
    };

    private static string? Valid(string? version) => version is not null && Version.TryParse(version, out _) ? version : null;
}
