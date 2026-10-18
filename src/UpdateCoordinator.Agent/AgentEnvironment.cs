using System.Net.NetworkInformation;
using System.Reflection;

namespace UpdateCoordinator.Agent;

/// <summary>
/// Basic facts about the local server and the agent, sent with register and heartbeat.
/// </summary>
internal interface IAgentEnvironment
{
    string HostName { get; }

    string? DomainName { get; }

    string OsVersion { get; }

    string AgentVersion { get; }
}

internal sealed class AgentEnvironment : IAgentEnvironment
{
    public string HostName => Environment.MachineName;

    public string? DomainName
    {
        get
        {
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return string.IsNullOrWhiteSpace(domain) ? null : domain;
        }
    }

    public string OsVersion => Environment.OSVersion.Version.ToString();

    public string AgentVersion { get; } = typeof(AgentEnvironment).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
