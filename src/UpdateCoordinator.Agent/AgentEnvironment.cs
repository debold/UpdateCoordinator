using System.Globalization;
using System.Net.NetworkInformation;
using System.Reflection;

using UpdateCoordinator.Agent.Inventory;

namespace UpdateCoordinator.Agent;

/// <summary>
/// Basic facts about the local server and the agent, sent with register, heartbeat and inventory.
/// </summary>
internal interface IAgentEnvironment
{
    string HostName { get; }

    string? DomainName { get; }

    /// <summary>Full OS version including UBR, e.g. 10.0.20348.2700; changes with every cumulative update.</summary>
    string OsVersion { get; }

    string AgentVersion { get; }

    /// <summary>True if Windows reports a pending reboot (component servicing or Windows Update).</summary>
    bool RebootPending { get; }

    /// <summary>Last start of the operating system.</summary>
    DateTimeOffset LastBootAt { get; }
}

internal sealed class AgentEnvironment(ISystemProbe probe) : IAgentEnvironment
{
    internal const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public string HostName => Environment.MachineName;

    public string? DomainName
    {
        get
        {
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return string.IsNullOrWhiteSpace(domain) ? null : domain;
        }
    }

    public string OsVersion
    {
        get
        {
            var version = Environment.OSVersion.Version;
            var ubr = probe.GetRegistryValue(CurrentVersionPath, "UBR");
            return int.TryParse(ubr, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                ? string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}.{revision}")
                : string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}");
        }
    }

    public string AgentVersion { get; } = typeof(AgentEnvironment).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Registry keys Windows sets while a reboot is pending. PendingFileRenameOperations is left out: many products set it routinely.</summary>
    internal static readonly IReadOnlyList<string> RebootPendingKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
    ];

    public bool RebootPending => RebootPendingKeys.Any(probe.RegistryKeyExists);

    public DateTimeOffset LastBootAt => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
