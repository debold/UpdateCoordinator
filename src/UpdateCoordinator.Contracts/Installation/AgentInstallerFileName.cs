using System.Globalization;
using System.Text.RegularExpressions;

namespace UpdateCoordinator.Contracts.Installation;

/// <summary>
/// The agent installer downloaded from the central server carries the central server address and the fingerprint of its
/// trust anchor in its file name, e.g. <c>UpdateCoordinator.Agent-0.3.0_uc01.lab.local_8443_0123456789abcdef0123456789abcdef.msi</c>
/// (see docs/03-communication-protocol.md, section 3.3). The file content stays unchanged.
/// </summary>
/// <remarks>
/// Also compiled into the installer custom action (.NET Framework 4.6.2), so only APIs available there are used.
/// A file name must never make the installation fail: anything unexpected only means "no address from the file name".
/// </remarks>
public static class AgentInstallerFileName
{
    /// <summary>Name part before the address.</summary>
    public const string Prefix = "UpdateCoordinator.Agent";

    /// <summary>Hex characters of the trust anchor fingerprint (the first 16 bytes of its SHA-256).</summary>
    public const int FingerprintLength = 32;

    /// <summary>Upper bound for the central server address, as enforced by the agent for the registry value.</summary>
    public const int MaxUrlLength = 2048;

    // Version, host, port and fingerprint; browsers may append " (1)" or "(1)" to a repeated download.
    private static readonly Regex Pattern = new(
        @"^UpdateCoordinator\.Agent(?:-[0-9A-Za-z.+-]{1,40})?_(?<host>[A-Za-z0-9.-]{1,253})_(?<port>[0-9]{1,5})(?:_(?<fingerprint>[0-9A-Fa-f]{32}))?(?:\s?\([0-9]{1,4}\))?\.msi$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Label = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>File name for a download; null if the address cannot be expressed in a file name (no IP v6, no path).</summary>
    public static string? Create(string version, Uri centralServer, string anchorFingerprint)
    {
        if (version is null || centralServer is null || anchorFingerprint is null)
        {
            return null;
        }

        if (centralServer.Scheme != "https" || centralServer.AbsolutePath != "/" || !string.IsNullOrEmpty(centralServer.Query)
            || centralServer.HostNameType != UriHostNameType.Dns && centralServer.HostNameType != UriHostNameType.IPv4
            || !IsHost(centralServer.Host) || anchorFingerprint.Length != FingerprintLength || !IsHex(anchorFingerprint)
            || !Regex.IsMatch(version, "^[0-9A-Za-z.+-]{1,40}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}-{1}_{2}_{3}_{4}.msi",
            Prefix,
            version,
            centralServer.Host.ToLowerInvariant(),
            centralServer.Port,
            anchorFingerprint.ToLowerInvariant());
    }

    /// <summary>Reads address and fingerprint from a path or file name; false for every name that does not match exactly.</summary>
    public static bool TryParse(string? path, out string? centralServerUrl, out string? anchorFingerprint)
    {
        centralServerUrl = null;
        anchorFingerprint = null;
        if (string.IsNullOrEmpty(path) || path!.Length > 1024)
        {
            return false;
        }

        var name = path.Substring(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1);
        Match match;
        try
        {
            match = Pattern.Match(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success
            || !IsHost(match.Groups["host"].Value)
            || !int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port < 1 || port > 65535)
        {
            return false;
        }

        centralServerUrl = string.Format(CultureInfo.InvariantCulture, "https://{0}:{1}/", match.Groups["host"].Value.ToLowerInvariant(), port);
        anchorFingerprint = match.Groups["fingerprint"].Success ? match.Groups["fingerprint"].Value.ToLowerInvariant() : null;
        return true;
    }

    /// <summary>An address the installer accepts from the dialog or command line: absolute https, a host, no user information.</summary>
    public static bool IsValidCentralServerUrl(string? value) =>
        !string.IsNullOrEmpty(value)
        && value!.Length <= MaxUrlLength
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https"
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.HostNameType == UriHostNameType.IPv6 || IsHost(uri.Host));

    /// <summary>True for a valid DNS host name or IP v4 address (labels of letters, digits and hyphens).</summary>
    public static bool IsHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host[0] == '.' || host[host.Length - 1] == '.')
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (!Label.IsMatch(label))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
