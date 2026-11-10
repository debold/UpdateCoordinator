using System.Net;

using Microsoft.Extensions.Options;
using Microsoft.Win32;

using SUSyphos.Common;

namespace SUSyphos.Agent.Trust;

/// <summary>A place in the zone map of the SYSTEM account. Separate so tests never touch the machine.</summary>
internal interface ISystemZoneMap
{
    /// <summary>The host the agent put into the intranet zone, from its own note; null if none.</summary>
    string? AddedHost { get; set; }

    void AddToIntranet(string host);

    void RemoveFromIntranet(string host);
}

/// <summary>
/// Puts the central server into the "Local intranet" zone of the SYSTEM account (Marc, 2026-11-10, default for every
/// server that uses WinGet). WinGet marks every installer it downloads with the zone of its address; an address with
/// dots is in the Internet zone, and Windows then shows the "Open File - Security Warning" before the installer starts.
/// Under SYSTEM nobody sees that dialog, so the installation hung (lab, Server 2019, 7-Zip exe installer). Exactly the
/// host of the central server is added, only for SYSTEM; the installers there are checked against their SHA-256 and
/// scanned before the source offers them. The agent removes its entry when the address changes, software management is
/// switched off or the agent is uninstalled, recognised by its own note.
/// </summary>
internal sealed partial class CentralZoneTrust(IOptionsMonitor<CentralServerOptions> central, ISystemZoneMap map, ILogger<CentralZoneTrust> logger)
{
    /// <summary>Applies the entry; call once per cycle with whether software management is on, and with false on uninstall.</summary>
    public void Apply(bool enabled)
    {
        var wanted = enabled ? Host(central.CurrentValue.BaseAddress) : null;
        var added = map.AddedHost;
        if (added is not null && !string.Equals(added, wanted, StringComparison.OrdinalIgnoreCase))
        {
            map.RemoveFromIntranet(added);
            map.AddedHost = null;
            LogRemoved(added);
        }

        if (wanted is not null && map.AddedHost is null)
        {
            map.AddToIntranet(wanted);
            map.AddedHost = wanted;
            LogAdded(wanted);
        }
    }

    /// <summary>The host to add: a name with dots or an IPv4 address; single-label names are intranet already.</summary>
    internal static string? Host(Uri? address) =>
        address?.Host is { Length: > 0 } host && host.Contains('.', StringComparison.Ordinal) && (IPAddress.TryParse(host, out var ip) ? ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork : Uri.CheckHostName(host) == UriHostNameType.Dns)
            ? host.ToLowerInvariant()
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Added the central server {Host} to the local intranet zone of SYSTEM, so WinGet can start the installers it downloads from there.")]
    private partial void LogAdded(string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the central server {Host} the agent had added to the local intranet zone of SYSTEM.")]
    private partial void LogRemoved(string host);
}

/// <summary>
/// The zone map of SYSTEM (HKEY_USERS\S-1-5-18), in "Domains" and in "EscDomains" (used while the enhanced security
/// configuration of Internet Explorer is on, the default on Windows Server), and a note file in the agent's data folder.
/// </summary>
internal sealed class SystemZoneMap : ISystemZoneMap
{
    internal const string ZoneMapKey = @"S-1-5-18\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap";

    /// <summary>The name of the range key for an IP address.</summary>
    internal const string RangeName = "SUSyphos";

    private const int IntranetZone = 1;

    private static readonly string NotePath = Path.Combine(DataPaths.Agent, "intranet-zone-central.txt");

    public string? AddedHost
    {
        get => File.Exists(NotePath) && File.ReadAllText(NotePath).Trim() is { Length: > 0 and <= 255 } host && CentralZoneTrust.Host(new UriBuilder("https", host).Uri) == host ? host : null;
        set
        {
            if (value is null)
            {
                File.Delete(NotePath);
            }
            else
            {
                Directory.CreateDirectory(DataPaths.Agent);
                File.WriteAllText(NotePath, value);
            }
        }
    }

    public void AddToIntranet(string host)
    {
        foreach (var path in Paths(host))
        {
            using var key = Registry.Users.CreateSubKey(path.Key, writable: true);
            if (path.Range is { } range)
            {
                key.SetValue(":Range", range, RegistryValueKind.String);
            }

            key.SetValue("https", IntranetZone, RegistryValueKind.DWord);
        }
    }

    public void RemoveFromIntranet(string host)
    {
        foreach (var path in Paths(host))
        {
            using (var key = Registry.Users.OpenSubKey(path.Key, writable: true))
            {
                if (key is null)
                {
                    continue;
                }

                key.DeleteValue("https", throwOnMissingValue: false);
                if (path.Range is not null)
                {
                    key.DeleteValue(":Range", throwOnMissingValue: false);
                }
            }

            DeleteEmpty(path.Key, path.Root);
        }
    }

    /// <summary>The keys of a host: Domains\&lt;domain&gt;\&lt;rest&gt; for a name, Ranges\SUSyphos for an IP address.</summary>
    internal static IEnumerable<(string Key, string Root, string? Range)> Paths(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            yield return ($@"{ZoneMapKey}\Ranges\{RangeName}", $@"{ZoneMapKey}\Ranges", host);
            yield return ($@"{ZoneMapKey}\EscRanges\{RangeName}", $@"{ZoneMapKey}\EscRanges", host);
            yield break;
        }

        var labels = host.Split('.');
        var domain = string.Join('.', labels[^2..]);
        var rest = labels.Length > 2 ? string.Join('.', labels[..^2]) : null;
        foreach (var map in new[] { "Domains", "EscDomains" })
        {
            var root = $@"{ZoneMapKey}\{map}";
            yield return (rest is null ? $@"{root}\{domain}" : $@"{root}\{domain}\{rest}", root, null);
        }
    }

    /// <summary>Deletes the key and its parents up to the root while they hold nothing else.</summary>
    private static void DeleteEmpty(string path, string root)
    {
        while (path.Length > root.Length)
        {
            using (var key = Registry.Users.OpenSubKey(path))
            {
                if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
                {
                    return;
                }
            }

            Registry.Users.DeleteSubKey(path, throwOnMissingSubKey: false);
            path = path[..path.LastIndexOf('\\')];
        }
    }
}
