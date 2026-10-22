using Microsoft.Extensions.Options;

using SUSyphos.Common;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Configuration;

/// <summary>
/// Agent settings set on the central server (<see cref="AgentSettings"/>), as a configuration source above the agent's
/// own files. The last values are kept in a protected file, so they apply from the start, also to logging, before the
/// central server is reached. Only known settings with valid values get in.
/// </summary>
internal sealed class CentralSettingsSource : IConfigurationSource
{
    private readonly CentralSettingsProvider _provider;

    public CentralSettingsSource(IReadOnlyDictionary<string, string>? initial) => _provider = new CentralSettingsProvider(initial);

    public CentralSettingsProvider Provider => _provider;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;
}

internal sealed class CentralSettingsProvider : ConfigurationProvider
{
    public CentralSettingsProvider(IReadOnlyDictionary<string, string>? initial)
    {
        Current = AgentSettings.Clean(initial);
        Data = ToConfiguration(Current);
    }

    /// <summary>The normalized central values by setting key.</summary>
    public IReadOnlyDictionary<string, string> Current { get; private set; }

    /// <summary>Replaces the central values; returns the keys that changed. Options built on IOptionsMonitor see the new values at once.</summary>
    public IReadOnlyList<string> Apply(IReadOnlyDictionary<string, string>? values)
    {
        var next = AgentSettings.Clean(values);
        var changed = next.Keys.Union(Current.Keys)
            .Where(key => !string.Equals(next.GetValueOrDefault(key), Current.GetValueOrDefault(key), StringComparison.Ordinal))
            .ToList();
        if (changed.Count == 0)
        {
            return changed;
        }

        Current = next;
        Data = ToConfiguration(next);
        OnReload();
        return changed;
    }

    private static Dictionary<string, string?> ToConfiguration(IReadOnlyDictionary<string, string> values) =>
        values.Select(v => (Definition: AgentSettings.Find(v.Key)!, v.Value))
            .ToDictionary(v => v.Definition.ConfigurationPath, v => (string?)v.Definition.ToConfiguration(v.Value), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Applies the central settings of each release list and keeps them for the next start.</summary>
internal sealed partial class CentralSettings(CentralSettingsProvider provider, ProtectedJsonFile<Dictionary<string, string>>? file, Actions.IServiceRestarter? restarter, ILogger<CentralSettings> logger)
{
    /// <summary>Default location: %ProgramData%\SUSyphos\Agent\central-settings.dat.</summary>
    public static string DefaultPath { get; } = Path.Combine(DataPaths.Agent, "central-settings.dat");

    /// <summary>A setting changed that only takes effect after the agent service restarted (logging).</summary>
    public bool RestartNeeded { get; private set; }

    public IReadOnlyDictionary<string, string> Current => provider.Current;

    public void Apply(IReadOnlyDictionary<string, string>? values)
    {
        var changed = provider.Apply(values);
        if (changed.Count == 0)
        {
            return;
        }

        try
        {
            file?.Save(new Dictionary<string, string>(provider.Current, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // The values apply now; after a restart the agent waits for the central server to send them again.
            LogSaveFailed(ex);
        }

        LogApplied(changed);

        if (changed.Any(key => AgentSettings.Find(key)?.NeedsRestart == true))
        {
            // Logging and process priority are set up when the agent starts: the service restarts with the new values.
            RestartNeeded = true;
            LogRestarting();
            restarter?.RequestRestart();
        }
    }

    /// <summary>Reads the kept values before the host is built; missing or unreadable file means none.</summary>
    public static Dictionary<string, string>? LoadSaved(ProtectedJsonFile<Dictionary<string, string>> file) => file.Load();

    [LoggerMessage(Level = LogLevel.Information, Message = "Central agent settings changed: {Keys}.")]
    private partial void LogApplied(IReadOnlyList<string> keys);

    [LoggerMessage(Level = LogLevel.Information, Message = "The agent service restarts to apply central logging or priority settings.")]
    private partial void LogRestarting();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The central agent settings could not be kept for the next start.")]
    private partial void LogSaveFailed(Exception exception);
}

/// <summary>
/// <see cref="IOptions{TOptions}"/> that always returns the current value of the options monitor, so every component that
/// reads <c>Value</c> sees central settings as soon as they arrive.
/// </summary>
internal sealed class LiveOptions<T>(IOptionsMonitor<T> monitor) : IOptions<T> where T : class
{
    public T Value => monitor.CurrentValue;
}
