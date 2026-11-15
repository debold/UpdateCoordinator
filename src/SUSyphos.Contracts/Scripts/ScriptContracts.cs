using System.Text.Json.Serialization;

namespace SUSyphos.Contracts.Scripts;

/// <summary>When a script of a WinGet package runs (Marc, 2026-11-13): before or after its installation or uninstallation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScriptPhase>))]
public enum ScriptPhase
{
    BeforeInstall = 0,
    AfterInstall = 1,
    BeforeUninstall = 2,
    AfterUninstall = 3,
}

/// <summary>For the installation phases: on a new installation, an upgrade or both.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScriptRunOn>))]
public enum ScriptRunOn
{
    Both = 0,
    Install = 1,
    Upgrade = 2,
}

/// <summary>What a failed script means for the installation or uninstallation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScriptOnFailure>))]
public enum ScriptOnFailure
{
    /// <summary>Before: the installation or uninstallation does not run. After: the result counts as failed.</summary>
    Stop = 0,

    /// <summary>Only for "before installation": install anyway.</summary>
    Continue = 1,
}

/// <summary>The action a script runs for, as the agent passes it in SUSYPHOS_ACTION.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScriptAction>))]
public enum ScriptAction
{
    Install = 0,
    Upgrade = 1,
    Uninstall = 2,
}

/// <summary>How a script run ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScriptRunResult>))]
public enum ScriptRunResult
{
    Succeeded = 0,

    /// <summary>Exit code 3010 or 1641: succeeded, a restart is needed (it waits for the maintenance window).</summary>
    SucceededRestart = 1,

    Failed = 2,

    /// <summary>Stopped after its timeout; the whole process tree was ended (0x800705B4).</summary>
    TimedOut = 3,

    /// <summary>Not run: the local veto (AllowScripts = 0) forbids scripts on this server.</summary>
    Blocked = 4,

    /// <summary>Not run: the script, its signature or its key did not verify, or it was older than one seen before.</summary>
    NotVerified = 5,
}

/// <summary>A script parameter as the script page defines it.</summary>
/// <param name="Name">Letters, digits and underscore, starting with a letter, at most 64 characters.</param>
/// <param name="Secret">A secret value: stored encrypted, never shown again, masked in the output.</param>
/// <param name="Required">The run needs a value (from the assignment or the default).</param>
/// <param name="Default">Value when the assignment sets none; never for secrets.</param>
/// <param name="Description">What it is for, shown in the assignment dialog.</param>
public sealed record ScriptParameter(string Name, bool Secret, bool Required, string? Default, string? Description);

/// <summary>
/// A script an agent may run for a software release or uninstallation: which approved revision, its hashes and the
/// parameter values for this server. Secret values travel only over TLS and are kept in memory only.
/// </summary>
public sealed record ScriptReference(ScriptPhase Phase, int ScriptId, int Revision, string ContentSha256, string ParametersSha256, IReadOnlyDictionary<string, string>? Values = null);

/// <summary>
/// An approved script revision as the agent downloads it (<c>GET /api/v1/scripts/{id}/{revision}</c>): the signed
/// content and fields, the revision signature by the script key, and the key with its certificate by the trust anchor.
/// </summary>
public sealed record ScriptPackage
{
    public required int ScriptId { get; init; }

    public required int Revision { get; init; }

    public required string PackageId { get; init; }

    public required ScriptPhase Phase { get; init; }

    public required ScriptRunOn RunOn { get; init; }

    public required ScriptOnFailure OnFailure { get; init; }

    public required int TimeoutSeconds { get; init; }

    /// <summary>The script as signed: UTF-8 with BOM and CRLF line ends, Base64.</summary>
    public required string Content { get; init; }

    /// <summary>The parameter definitions as signed (JSON array of <see cref="ScriptParameter"/>).</summary>
    public required string ParametersJson { get; init; }

    public required DateTimeOffset SignedAt { get; init; }

    /// <summary>Signature of the revision by the script key (IEEE P1363, Base64).</summary>
    public required string Signature { get; init; }

    public required string KeyId { get; init; }

    /// <summary>Public key of the script key (SubjectPublicKeyInfo, Base64).</summary>
    public required string KeyPublicKey { get; init; }

    public required DateTimeOffset KeyCreatedAt { get; init; }

    /// <summary>Signature of the key certificate by the trust anchor (Base64).</summary>
    public required string KeyCertificate { get; init; }
}

/// <summary>One finished script run, reported by the agent (<c>POST /api/v1/agents/script-runs</c>).</summary>
public sealed record ScriptRunReport
{
    public required Guid RunId { get; init; }

    public required string PackageId { get; init; }

    public required ScriptPhase Phase { get; init; }

    public required ScriptAction Action { get; init; }

    public int? ScriptId { get; init; }

    public int? Revision { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset FinishedAt { get; init; }

    public int? ExitCode { get; init; }

    public required ScriptRunResult Result { get; init; }

    /// <summary>The complete output, one record per line with time and stream, secrets masked; at most <see cref="ScriptLimits.MaxOutputBytes"/> (head and tail beyond).</summary>
    public string? Output { get; init; }

    public bool Truncated { get; init; }

    /// <summary>Why it did not run or failed, in one sentence.</summary>
    public string? Detail { get; init; }
}

/// <summary>Limits shared by central server and agent.</summary>
public static class ScriptLimits
{
    /// <summary>Largest script (UTF-8 bytes).</summary>
    public const int MaxContentBytes = 256 * 1024;

    /// <summary>Largest output of one run the central server keeps; beyond, head and tail with a note.</summary>
    public const int MaxOutputBytes = 1024 * 1024;

    public const int MaxParameters = 20;

    public const int MaxValueLength = 2048;

    public const int MinTimeoutSeconds = 60;

    public const int DefaultTimeoutSeconds = 300;

    public const int MaxTimeoutSeconds = 1800;

    /// <summary>Exit code Windows uses for a timeout (ERROR_TIMEOUT as HRESULT).</summary>
    public const int TimeoutCode = unchecked((int)0x800705B4);

    /// <summary>Installation result: the script before it failed, so WinGet did not run (custom HRESULT, facility 5).</summary>
    public const int BeforeScriptFailedCode = unchecked((int)0xA0050001);

    /// <summary>Installation result: WinGet installed it, but the script after it failed.</summary>
    public const int AfterScriptFailedCode = unchecked((int)0xA0050002);

    /// <summary>Installation result: the package has scripts and the local veto forbids scripts on this server.</summary>
    public const int ScriptsBlockedCode = unchecked((int)0xA0050003);

    /// <summary>Installation result: a script did not verify (signature, hashes, older revision), so nothing ran.</summary>
    public const int ScriptNotVerifiedCode = unchecked((int)0xA0050004);

    /// <summary>The text that replaces secret values in outputs.</summary>
    public const string Mask = "********";
}
