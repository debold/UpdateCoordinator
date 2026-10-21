using System.Reflection;

namespace SUSyphos.Server.Administration;

/// <summary>Version of the running central server, taken from the git tag at build time (MinVer).</summary>
public static class ServerVersion
{
    /// <summary>The full informational version including the commit, for example <c>0.7.2+3f1c2ab</c>.</summary>
    public static string Full { get; } =
        typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The version without the build metadata, for example <c>0.7.2</c>.</summary>
    public static string Display { get; } = WithoutBuildMetadata(Full);

    /// <summary>Cuts the build metadata (everything from <c>+</c>) off a semantic version.</summary>
    public static string WithoutBuildMetadata(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
