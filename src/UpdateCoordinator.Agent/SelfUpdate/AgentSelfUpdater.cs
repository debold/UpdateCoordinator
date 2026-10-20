using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent.Identity;
using UpdateCoordinator.Common;
using UpdateCoordinator.Contracts.Agents;

namespace UpdateCoordinator.Agent.SelfUpdate;

/// <summary>Agent self-update settings. Bound from the "AgentUpdate" configuration section.</summary>
internal sealed class AgentUpdateOptions
{
    public const string SectionName = "AgentUpdate";

    /// <summary>Switches self-updates off on this server; the central server then shows the old version.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A failed update of one version is tried again after this delay.</summary>
    [Range(typeof(TimeSpan), "00:10:00", "7.00:00:00")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Upper bound for a package; a larger announcement is refused.</summary>
    [Range(1L * 1024 * 1024, 1024L * 1024 * 1024)]
    public long MaxPackageSize { get; set; } = 500L * 1024 * 1024;

    /// <summary>How long msiexec may run before the attempt counts as failed.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "02:00:00")]
    public TimeSpan InstallTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Folder for the downloaded package and the msiexec log; recreated for every update with an administrators-only ACL.</summary>
    public string Directory { get; set; } = Path.Combine(DataPaths.Agent, "Update");
}

/// <summary>The last update attempt, kept across the restart the installation causes.</summary>
internal sealed record AgentUpdateState(string Version, DateTime AttemptedAtUtc, bool Pending, string? Error);

/// <summary>Downloads an announced package from the central server; replaced by a fake in tests.</summary>
internal interface IAgentPackageClient
{
    /// <summary>Writes the package to <paramref name="target"/>. Returns false if the central server no longer accepts the credentials.</summary>
    Task<bool> DownloadAsync(AgentIdentity identity, string version, Stream target, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>Runs the MSI; replaced by a fake in tests, so no test ever installs anything.</summary>
internal interface IPackageInstaller
{
    /// <summary>Starts msiexec and waits for it; returns its exit code, or null on timeout. The agent service is usually stopped before.</summary>
    Task<int?> InstallAsync(string packagePath, string logPath, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Persists <see cref="AgentUpdateState"/> as a small JSON file.</summary>
internal interface IAgentUpdateStateStore
{
    AgentUpdateState? Load();

    void Save(AgentUpdateState state);

    void Delete();
}

/// <summary>
/// Updates the agent to the version the central server announces in the heartbeat (docs/05-agent-design.md, section 7):
/// downloads the package from the central server, checks size and SHA-256, and runs the MSI, which stops this service,
/// replaces the files and starts the new version. The outcome is known after the restart: the running version tells
/// whether the update worked. Failures are reported with the heartbeat and retried after <see cref="AgentUpdateOptions.RetryDelay"/>.
/// </summary>
internal sealed partial class AgentSelfUpdater(
    IAgentPackageClient packages,
    IPackageInstaller installer,
    IAgentUpdateStateStore stateStore,
    IAgentEnvironment environment,
    IOptions<AgentUpdateOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentSelfUpdater> logger)
{
    private readonly Lock _lock = new();
    private bool _startupChecked;

    /// <summary>Error of the last failed attempt for the heartbeat; null if none.</summary>
    public string? LastError
    {
        get
        {
            CheckOutcomeAfterStart();
            return stateStore.Load()?.Error;
        }
    }

    /// <summary>Runs an announced update. Returns false if the central server no longer accepts the credentials.</summary>
    public async Task<bool> UpdateIfAnnouncedAsync(AgentIdentity identity, HeartbeatResponse heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        CheckOutcomeAfterStart();
        var settings = options.Value;
        if (!settings.Enabled || heartbeat.State != RegistrationState.Approved || heartbeat.AgentUpdate is not { } update || !IsValid(update, settings))
        {
            return true;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var state = stateStore.Load();
        if (state is not null && state.Version == update.Version && now - state.AttemptedAtUtc < settings.RetryDelay)
        {
            return true;
        }

        LogStarting(update.Version);
        var directory = PrepareDirectory(settings.Directory);
        var packagePath = Path.Combine(directory, "UpdateCoordinator.Agent.msi");
        string? error;
        // The handle stays open with read sharing only until msiexec is done, so the verified file cannot be swapped.
        await using (var file = new FileStream(packagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 81920, useAsync: true))
        {
            if (!await packages.DownloadAsync(identity, update.Version, file, update.Size, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            error = await VerifyAsync(file, update, cancellationToken).ConfigureAwait(false);
            if (error is null)
            {
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                stateStore.Save(new AgentUpdateState(update.Version, now, Pending: true, Error: null));
                var exitCode = await installer.InstallAsync(packagePath, Path.Combine(directory, "install.log"), settings.InstallTimeout, cancellationToken).ConfigureAwait(false);
                error = exitCode switch
                {
                    // Success that did not stop this process (e.g. the service was not restarted): the next start decides.
                    0 or 3010 => null,
                    null => "msiexec did not finish in time.",
                    var code => $"msiexec ended with exit code {code}; see {Path.Combine(directory, "install.log")}.",
                };
            }
        }

        if (error is not null)
        {
            stateStore.Save(new AgentUpdateState(update.Version, now, Pending: false, error));
            LogFailed(update.Version, error);
        }

        return true;
    }

    /// <summary>After a restart: the version now running tells whether the pending installation worked.</summary>
    private void CheckOutcomeAfterStart()
    {
        lock (_lock)
        {
            if (_startupChecked)
            {
                return;
            }

            _startupChecked = true;
        }

        var state = stateStore.Load();
        if (state is not { Pending: true })
        {
            return;
        }

        var running = environment.AgentVersion.Split('+')[0];
        if (string.Equals(running, state.Version, StringComparison.OrdinalIgnoreCase))
        {
            stateStore.Delete();
            TryDeleteDirectory(options.Value.Directory);
            LogSucceeded(state.Version);
        }
        else
        {
            var error = $"The installation of agent {state.Version} did not complete; agent {running} is still running. See {Path.Combine(options.Value.Directory, "install.log")}.";
            stateStore.Save(state with { Pending = false, Error = error });
            LogFailed(state.Version, error);
        }
    }

    private static bool IsValid(AgentUpdate update, AgentUpdateOptions settings) =>
        update.Size > 0
        && update.Size <= settings.MaxPackageSize
        && update.Sha256 is { Length: 64 }
        && update.Sha256.All(Uri.IsHexDigit)
        && update.Version is { Length: > 0 and <= 40 }
        && update.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    private static async Task<string?> VerifyAsync(FileStream file, AgentUpdate update, CancellationToken cancellationToken)
    {
        if (file.Length != update.Size)
        {
            return $"The downloaded package has {file.Length} bytes instead of {update.Size}.";
        }

        file.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        return string.Equals(hash, update.Sha256, StringComparison.OrdinalIgnoreCase) ? null : "The downloaded package does not match its SHA-256.";
    }

    /// <summary>
    /// Recreates the folder with access for SYSTEM and administrators only, so no other account can place or swap the MSI
    /// that runs as LocalSystem, even if it created the folder before.
    /// </summary>
    private static string PrepareDirectory(string directory)
    {
        TryDeleteDirectory(directory);
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        // The service runs as LocalSystem; the account running the agent is added so the same code works in tests.
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        var accounts = new[]
        {
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            current.User!,
        };
        foreach (var sid in accounts.Distinct())
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                sid,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        System.IO.FileSystemAclExtensions.Create(new DirectoryInfo(directory), security);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Updating the agent to version {Version} as released by the central server.")]
    private partial void LogStarting(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "The agent was updated to version {Version}.")]
    private partial void LogSucceeded(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updating the agent to version {Version} failed: {Error}")]
    private partial void LogFailed(string version, string error);
}

/// <summary>Stores the update state as JSON next to the other agent data; an unreadable file counts as no state.</summary>
internal sealed class JsonAgentUpdateStateStore(string path) : IAgentUpdateStateStore
{
    public static string DefaultPath { get; } = Path.Combine(DataPaths.Agent, "agent-update.json");

    public AgentUpdateState? Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096)
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<AgentUpdateState>(File.ReadAllText(path));
            return state is { Version.Length: > 0 and <= 40 } && (state.Error?.Length ?? 0) <= 512 ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(AgentUpdateState state)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete() => File.Delete(path);
}

/// <summary>Runs msiexec silently with a verbose log; the arguments contain only paths the agent created.</summary>
internal sealed class MsiexecInstaller : IPackageInstaller
{
    public async Task<int?> InstallAsync(string packagePath, string logPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/i", packagePath, "/qn", "/norestart", "/l*v", logPath })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("msiexec could not be started.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
