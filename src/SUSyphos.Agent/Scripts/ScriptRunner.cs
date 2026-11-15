using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

using Microsoft.Win32;

using SUSyphos.Agent.Identity;
using SUSyphos.Agent.Trust;
using SUSyphos.Common;
using SUSyphos.Contracts.Scripts;

namespace SUSyphos.Agent.Scripts;

/// <summary>One script to run around an installation or uninstallation.</summary>
internal sealed record ScriptRunRequest(string PackageId, ScriptAction Action, string? Version, string? PreviousVersion, int? InstallerExitCode, ScriptReference Script);

/// <summary>How a script run ended, for the installation that waits on it.</summary>
/// <param name="Ran">False when the script was skipped because it is not for this action (install or upgrade only).</param>
/// <param name="Result">The result of the run.</param>
/// <param name="OnFailure">What a failure means, as signed.</param>
/// <param name="Detail">One sentence for the agent log.</param>
internal sealed record ScriptRunOutcome(bool Ran, ScriptRunResult Result, ScriptOnFailure OnFailure, string? Detail)
{
    public bool Succeeded => !Ran || Result is ScriptRunResult.Succeeded or ScriptRunResult.SucceededRestart;

    public bool RestartRequired => Ran && Result == ScriptRunResult.SucceededRestart;

    public static readonly ScriptRunOutcome Skipped = new(false, ScriptRunResult.Succeeded, ScriptOnFailure.Stop, null);
}

/// <summary>Runs scripts of WinGet packages. Separate so the installers can be tested without scripts.</summary>
internal interface IScriptRunner
{
    Task<ScriptRunOutcome> RunAsync(ScriptRunRequest request, CancellationToken cancellationToken);
}

/// <summary>The highest revision seen per package and phase, so an older (replayed) revision never runs again.</summary>
internal sealed record ScriptState(Dictionary<string, int> Highest);

/// <summary>
/// Runs an approved script of a WinGet package (Marc, 2026-11-13, concept https://claude.ai/artifact/D4zqEgR9uWNiUsG4D8TynE):
/// <list type="number">
/// <item>Local veto: <c>HKLM\SOFTWARE\Policies\SUSyphos\Agent\AllowScripts = 0</c> forbids scripts on this server.</item>
/// <item>Downloads the revision and verifies the chain trust anchor → script key → revision, the hashes the release named,
/// package and phase, and that it is not older than one seen before.</item>
/// <item>Writes it to a run folder only SYSTEM can change, keeps the file open against changes and hashes it again.</item>
/// <item>Runs Windows PowerShell 5.1 with the fixed wrapper in a job object; the timeout ends the whole tree.</item>
/// <item>Reports the run with its complete output, secret values masked, and keeps a log for 14 days.</item>
/// </list>
/// A local administrator can of course change anything on the server; the signature protects against a changed
/// central server database, a changed file in transit and a replayed older revision.
/// </summary>
internal sealed partial class ScriptRunner(
    IScriptClient client,
    IScriptProcess process,
    IAgentIdentityStore identities,
    ITrustStateStore trust,
    ProtectedJsonFile<ScriptState> state,
    TimeProvider timeProvider,
    ILogger<ScriptRunner> logger,
    string? folder = null) : IScriptRunner
{
    public const string PolicyKey = @"SOFTWARE\Policies\SUSyphos\Agent";
    public const string AllowScriptsValue = "AllowScripts";

    /// <summary>How long run logs stay on the server.</summary>
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(14);

    private readonly string _folder = folder ?? DataPaths.Agent;

    /// <summary>True when a group policy (or an administrator) set AllowScripts to 0 on this server.</summary>
    public static bool BlockedLocally()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
        return key?.GetValue(AllowScriptsValue) is int value && value == 0;
    }

    /// <summary>
    /// What a group policy for PowerShell forbids: scripts switched off, or only signed scripts allowed (AllSigned), which
    /// "-ExecutionPolicy Bypass" cannot override; null when scripts may run.
    /// </summary>
    public static string? PolicyBlocker()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\PowerShell");
        if (key is null)
        {
            return null;
        }

        return key.GetValue("EnableScripts") is int enabled && enabled == 0 ? "A group policy switched off PowerShell scripts on this server (EnableScripts = 0)."
            : key.GetValue("EnableScripts") is int on && on == 1 && string.Equals(key.GetValue("ExecutionPolicy") as string, "AllSigned", StringComparison.OrdinalIgnoreCase)
                ? "A group policy sets the PowerShell execution policy AllSigned; SUSyphos scripts are signed with their own key, not with Authenticode, so PowerShell refuses them."
                : null;
    }

    /// <summary>Injectable for tests; the real checks read the registry.</summary>
    internal Func<bool> IsBlockedLocally { get; init; } = BlockedLocally;

    internal Func<string?> PowerShellPolicyBlocker { get; init; } = PolicyBlocker;

    public async Task<ScriptRunOutcome> RunAsync(ScriptRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = timeProvider.GetUtcNow();
        var identity = identities.Load();
        if (identity is null)
        {
            return new ScriptRunOutcome(true, ScriptRunResult.NotVerified, ScriptOnFailure.Stop, "The agent is not registered.");
        }

        if (IsBlockedLocally())
        {
            return await FinishAsync(identity, request, null, started, null, ScriptRunResult.Blocked, "Scripts are forbidden on this server (AllowScripts = 0).", ScriptOnFailure.Stop, null, cancellationToken).ConfigureAwait(false);
        }

        ScriptPackage? package;
        try
        {
            package = await client.GetScriptAsync(identity, request.Script.ScriptId, request.Script.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return await FinishAsync(identity, request, null, started, null, ScriptRunResult.Failed, $"The script could not be downloaded: {exception.Message}", ScriptOnFailure.Stop, null, cancellationToken).ConfigureAwait(false);
        }

        var anchor = trust.Load().AnchorPublicKey;
        var problem = package is null ? "The central server did not hand out the script."
            : anchor is null ? "No trust anchor is pinned yet."
            : ScriptSigningFormat.Verify(package, Convert.FromBase64String(anchor), request.Script, request.PackageId);
        var stateKey = $"{request.PackageId.ToLowerInvariant()}|{request.Script.Phase}";
        var seen = state.Load()?.Highest ?? [];
        if (problem is null && seen.TryGetValue(stateKey, out var highest) && package!.Revision < highest)
        {
            problem = $"Revision {package.Revision} is older than revision {highest} run before; an older revision never runs again.";
        }

        if (problem is not null)
        {
            LogNotVerified(request.PackageId, request.Script.Phase, problem);
            return await FinishAsync(identity, request, package, started, null, ScriptRunResult.NotVerified, problem, ScriptOnFailure.Stop, null, cancellationToken).ConfigureAwait(false);
        }

        if (!AppliesTo(package!, request.Action))
        {
            return ScriptRunOutcome.Skipped;
        }

        seen[stateKey] = Math.Max(package!.Revision, seen.GetValueOrDefault(stateKey));
        state.Save(new ScriptState(seen));

        if (PowerShellPolicyBlocker() is { } policy)
        {
            return await FinishAsync(identity, request, package, started, null, ScriptRunResult.Failed, policy, package.OnFailure, null, cancellationToken).ConfigureAwait(false);
        }

        var secrets = SecretValues(package, request.Script);
        var runFolder = Path.Combine(_folder, "Scripts", $"run-{Guid.NewGuid():N}");
        try
        {
            CreateRunFolder(runFolder);
            var content = Convert.FromBase64String(package.Content);
            var scriptPath = Path.Combine(runFolder, "script.ps1");
            var wrapperPath = Path.Combine(runFolder, ScriptWrapper.FileName);
            File.WriteAllText(wrapperPath, ScriptWrapper.Text, new System.Text.UTF8Encoding(true));

            // Kept open with read sharing only until PowerShell ended, so nobody replaces it in between; hashed again
            // from the open file.
            await File.WriteAllBytesAsync(scriptPath, content, cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)), request.Script.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                return await FinishAsync(identity, request, package, started, null, ScriptRunResult.NotVerified, "The script file changed after it was written.", ScriptOnFailure.Stop, null, cancellationToken).ConfigureAwait(false);
            }

            var values = JsonSerializer.Serialize(request.Script.Values ?? new Dictionary<string, string>());
            var environment = new Dictionary<string, string>
            {
                ["SUSYPHOS_ACTION"] = request.Action.ToString(),
                ["SUSYPHOS_PACKAGE_ID"] = request.PackageId,
                ["SUSYPHOS_VERSION"] = request.Version ?? string.Empty,
                ["SUSYPHOS_PREVIOUS_VERSION"] = request.PreviousVersion ?? string.Empty,
                ["SUSYPHOS_ARCHITECTURE"] = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                ["SUSYPHOS_INSTALLER_EXIT_CODE"] = request.InstallerExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            };
            LogRunning(request.PackageId, request.Script.Phase, package.Revision, package.TimeoutSeconds);
            var ran = await process.RunAsync(wrapperPath, scriptPath, values, environment, TimeSpan.FromSeconds(package.TimeoutSeconds), cancellationToken).ConfigureAwait(false);
            var output = ran.Output.TrimStart('\uFEFF') + (string.IsNullOrWhiteSpace(ran.Errors) ? string.Empty : string.Join(Environment.NewLine, ran.Errors.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => ScriptOutput.Line(timeProvider.GetUtcNow(), "Error", line))));
            output = ScriptOutput.Mask(output, secrets);
            var (result, detail) = ran.ExitCode switch
            {
                null => (ScriptRunResult.TimedOut, $"Stopped after {package.TimeoutSeconds / 60} minutes with every process it started."),
                0 => (ScriptRunResult.Succeeded, (string?)null),
                3010 => (ScriptRunResult.SucceededRestart, "Succeeded; a restart is needed (it waits for the maintenance window)."),
                1641 => (ScriptRunResult.SucceededRestart, "Succeeded; the script said it started a restart (1641), which SUSyphos does not do from scripts: the restart waits for the maintenance window."),
                var code => (ScriptRunResult.Failed, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Exit code {code}.")),
            };
            return await FinishAsync(identity, request, package, started, ran.ExitCode ?? ScriptLimits.TimeoutCode, result, detail, package.OnFailure, output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or FormatException)
        {
            return await FinishAsync(identity, request, package, started, null, ScriptRunResult.Failed, $"The script could not run: {exception.Message}", package.OnFailure, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(runFolder);
        }
    }

    private static bool AppliesTo(ScriptPackage package, ScriptAction action) => package.RunOn switch
    {
        ScriptRunOn.Install => action == ScriptAction.Install,
        ScriptRunOn.Upgrade => action == ScriptAction.Upgrade,
        _ => true,
    };

    /// <summary>The values of the secret parameters, which never reach a log.</summary>
    private static List<string> SecretValues(ScriptPackage package, ScriptReference reference)
    {
        var secretNames = ScriptSigningFormat.ReadParameters(package.ParametersJson).Where(p => p.Secret).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return [.. (reference.Values ?? new Dictionary<string, string>()).Where(v => secretNames.Contains(v.Key)).Select(v => v.Value)];
    }

    private async Task<ScriptRunOutcome> FinishAsync(AgentIdentity identity, ScriptRunRequest request, ScriptPackage? package, DateTimeOffset started, int? exitCode, ScriptRunResult result, string? detail, ScriptOnFailure onFailure, string? output, CancellationToken cancellationToken)
    {
        var (limited, truncated) = ScriptOutput.Limit(output);
        var report = new ScriptRunReport
        {
            RunId = Guid.NewGuid(),
            PackageId = request.PackageId,
            Phase = request.Script.Phase,
            Action = request.Action,
            ScriptId = request.Script.ScriptId,
            Revision = package?.Revision ?? request.Script.Revision,
            StartedAt = started,
            FinishedAt = timeProvider.GetUtcNow(),
            ExitCode = exitCode,
            Result = result,
            Output = limited,
            Truncated = truncated,
            Detail = detail,
        };
        WriteLog(report);
        try
        {
            if (!await client.SendRunAsync(identity, report, cancellationToken).ConfigureAwait(false))
            {
                LogNotReported(request.PackageId, request.Script.Phase);
            }
        }
        catch (HttpRequestException exception)
        {
            LogNotReportedBecause(request.PackageId, request.Script.Phase, exception.Message);
        }

        LogFinished(request.PackageId, request.Script.Phase, result, detail ?? string.Empty);
        return new ScriptRunOutcome(true, result, onFailure, detail);
    }

    /// <summary>Keeps the run as a file under Logs\Scripts for 14 days (SYSTEM and administrators, like the agent logs).</summary>
    private void WriteLog(ScriptRunReport report)
    {
        try
        {
            var logs = Path.Combine(_folder, "Logs", "Scripts");
            Directory.CreateDirectory(logs);
            foreach (var old in new DirectoryInfo(logs).EnumerateFiles("*.log").Where(f => f.LastWriteTimeUtc < timeProvider.GetUtcNow().UtcDateTime - LogRetention))
            {
                old.Delete();
            }

            var name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{string.Concat(report.PackageId.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'))}-{report.Phase}-{report.FinishedAt.UtcDateTime:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(Path.Combine(logs, name), $"Result {report.Result}, exit code {report.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}{(report.Detail is null ? null : ": " + report.Detail)}{Environment.NewLine}{report.Output}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogNotWritten(exception.Message);
        }
    }

    /// <summary>A folder SYSTEM controls alone, administrators may read it; nothing inherited from ProgramData.</summary>
    private static void CreateRunFolder(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

        // The account the agent runs as (SYSTEM in operation, the developer in tests) keeps full control.
        using var current = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new DirectoryInfo(path).Create(security);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogNotDeleted(path, exception.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Script of {PackageId} ({Phase}, revision {Revision}) runs, timeout {Timeout} s.")]
    private partial void LogRunning(string packageId, ScriptPhase phase, int revision, int timeout);

    [LoggerMessage(Level = LogLevel.Information, Message = "Script of {PackageId} ({Phase}) finished: {Result}. {Detail}")]
    private partial void LogFinished(string packageId, ScriptPhase phase, ScriptRunResult result, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script of {PackageId} ({Phase}) not run: {Problem}")]
    private partial void LogNotVerified(string packageId, ScriptPhase phase, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The run of the script of {PackageId} ({Phase}) could not be reported; its log stays on the server.")]
    private partial void LogNotReported(string packageId, ScriptPhase phase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The run of the script of {PackageId} ({Phase}) could not be reported: {Reason}")]
    private partial void LogNotReportedBecause(string packageId, ScriptPhase phase, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The script run log could not be written: {Reason}")]
    private partial void LogNotWritten(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The run folder {Path} could not be deleted: {Reason}")]
    private partial void LogNotDeleted(string path, string reason);
}
