using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Amsi;
using SUSyphos.Contracts.Scripts;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;
using SUSyphos.Server.Software;

namespace SUSyphos.Server.Scripts;

/// <summary>A script of a package with its approved revision, for the package page and the script page.</summary>
internal sealed record PackageScriptView(PackageScriptEntity Script, PackageScriptRevisionEntity? Approved, IReadOnlyList<PackageScriptRevisionEntity> Revisions)
{
    /// <summary>The draft differs from the approved revision (or there is none yet).</summary>
    public bool HasDraft => Script.DraftContent is not null;

    /// <summary>The text an editor shows: the draft, otherwise the approved revision.</summary>
    public string Text => Script.DraftContent ?? (Approved is null ? string.Empty : ScriptSigningFormat.ContentText(Approved.Content));

    public IReadOnlyList<ScriptParameter> Parameters => ScriptSigningFormat.ReadParameters(Script.DraftParametersJson ?? Approved?.ParametersJson ?? "[]");
}

/// <summary>
/// Scripts of WinGet packages (Marc, 2026-11-13/14, concept https://claude.ai/artifact/D4zqEgR9uWNiUsG4D8TynE):
/// administrators write a draft and approve it; approval scans the script (AMSI), signs it with the script key and makes
/// it a new immutable revision that agents run before or after installing, upgrading or uninstalling the package. One
/// administrator approves (no four-eyes, no signing password). Values of parameters are set per assignment; secrets are
/// stored DPAPI-protected and never shown again. Off until switched on (<see cref="EnabledKey"/>). Everything audited.
/// </summary>
internal sealed class ScriptAdministration(
    ServerDbContext db,
    ScriptSigning signing,
    IInstallerScanner scanner,
    IOptions<SoftwareOptions> softwareOptions,
    AuditLog audit,
    TimeProvider timeProvider)
{
    public const string EnabledKey = "Scripts.Enabled";

    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("SUSyphos.Server.ScriptSecret.v1");

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        await db.Settings.AsNoTracking().Where(s => s.Key == EnabledKey).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) == "true";

    public async Task<Outcome> SetEnabledAsync(bool enabled, string actor, CancellationToken cancellationToken)
    {
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == EnabledKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = EnabledKey, Value = enabled ? "true" : "false" });
        }
        else
        {
            setting.Value = enabled ? "true" : "false";
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AnnounceAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, enabled ? "Script.Enabled" : "Script.Disabled", null, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Agents fetch their release list again, so they get approved, changed or deleted scripts and values.</summary>
    private Task<int> AnnounceAsync(CancellationToken cancellationToken) =>
        db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.ReleasesVersion, c => c.ReleasesVersion + 1), cancellationToken);

    public async Task<IReadOnlyList<PackageScriptView>> ListAsync(string packageId, CancellationToken cancellationToken)
    {
        var scripts = await db.PackageScripts.AsNoTracking().Where(s => s.PackageId == packageId).OrderBy(s => s.Phase).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = scripts.Select(s => s.Id).ToList();
        var revisions = await db.PackageScriptRevisions.AsNoTracking().Where(r => ids.Contains(r.ScriptId)).OrderByDescending(r => r.Revision).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. scripts.Select(s => new PackageScriptView(s, revisions.FirstOrDefault(r => r.ScriptId == s.Id && r.Revision == s.ApprovedRevision), [.. revisions.Where(r => r.ScriptId == s.Id)]))];
    }

    public async Task<PackageScriptView?> GetAsync(string packageId, ScriptPhase phase, CancellationToken cancellationToken) =>
        (await ListAsync(packageId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Script.Phase == phase);

    /// <summary>The checks of a draft; null when it can be saved.</summary>
    public static string? Validate(ScriptPhase phase, string? content, IReadOnlyList<ScriptParameter> parameters, ScriptRunOn runOn, ScriptOnFailure onFailure, int timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Enum.IsDefined(phase) || !Enum.IsDefined(runOn) || !Enum.IsDefined(onFailure))
        {
            return "The phase, when it runs or what a failure means is invalid.";
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return "Enter the script.";
        }

        if (ScriptSigningFormat.NormalizeContent(content).Length > ScriptLimits.MaxContentBytes)
        {
            return $"The script may have at most {ScriptLimits.MaxContentBytes / 1024} KB.";
        }

        if (content.Contains('\0', StringComparison.Ordinal))
        {
            return "The script contains a null character.";
        }

        if (runOn != ScriptRunOn.Both && phase is not (ScriptPhase.BeforeInstall or ScriptPhase.AfterInstall))
        {
            return "Only scripts around the installation can run on installations or upgrades only.";
        }

        if (onFailure == ScriptOnFailure.Continue && phase != ScriptPhase.BeforeInstall)
        {
            return "Only a script before the installation can let the installation run anyway.";
        }

        if (timeoutSeconds is < ScriptLimits.MinTimeoutSeconds or > ScriptLimits.MaxTimeoutSeconds)
        {
            return $"The timeout must be between {ScriptLimits.MinTimeoutSeconds / 60} and {ScriptLimits.MaxTimeoutSeconds / 60} minutes.";
        }

        if (parameters.Count > ScriptLimits.MaxParameters)
        {
            return $"A script may have at most {ScriptLimits.MaxParameters} parameters.";
        }

        foreach (var parameter in parameters)
        {
            if (!ScriptSigningFormat.IsParameterName(parameter.Name))
            {
                return $"\"{parameter.Name}\" is no valid parameter name (a letter, then letters, digits or underscores, at most 64).";
            }

            if (parameter.Secret && parameter.Default is not null)
            {
                return $"The secret parameter {parameter.Name} cannot have a default.";
            }

            if ((parameter.Default?.Length ?? 0) > ScriptLimits.MaxValueLength || (parameter.Description?.Length ?? 0) > 256)
            {
                return $"The default or description of {parameter.Name} is too long.";
            }
        }

        return parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice
            ? $"The parameter {twice.Key} is there twice."
            : null;
    }

    /// <summary>Saves the draft of a phase (creates the script on first save). Nothing reaches an agent before approval.</summary>
    public async Task<Outcome> SaveDraftAsync(string packageId, ScriptPhase phase, string? content, IReadOnlyList<ScriptParameter> parameters, ScriptRunOn runOn, ScriptOnFailure onFailure, int timeoutSeconds, string actor, CancellationToken cancellationToken, ScriptPowerShell powerShell = ScriptPowerShell.WindowsPowerShell)
    {
        if (!await db.Packages.AnyAsync(p => p.PackageId == packageId, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome("The package is not in the catalog.");
        }

        if (Validate(phase, content, parameters, runOn, onFailure, timeoutSeconds) is { } error)
        {
            return new Outcome(error);
        }

        var text = ScriptSigningFormat.ContentText(ScriptSigningFormat.NormalizeContent(content!));
        var script = await db.PackageScripts.SingleOrDefaultAsync(s => s.PackageId == packageId && s.Phase == phase, cancellationToken).ConfigureAwait(false);
        var before = script is null ? string.Empty : script.DraftContent ?? await ApprovedTextAsync(script, cancellationToken).ConfigureAwait(false);
        if (script is null)
        {
            script = new PackageScriptEntity { PackageId = packageId, Phase = phase, DraftChangedBy = actor };
            db.PackageScripts.Add(script);
        }

        script.DraftContent = text;
        script.DraftParametersJson = ScriptSigningFormat.ParametersJson(parameters);
        script.DraftRunOn = runOn;
        script.DraftOnFailure = onFailure;
        script.DraftTimeoutSeconds = timeoutSeconds;
        script.DraftPowerShell = Enum.IsDefined(powerShell) ? powerShell : ScriptPowerShell.WindowsPowerShell;
        script.DraftChangedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        script.DraftChangedBy = actor;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Script.Changed", $"{packageId} {phase}", DiffSummary(before, text), cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>"3 lines added, 1 removed": a summary of the change for the audit log and the approve dialog.</summary>
    public static string DiffSummary(string before, string after)
    {
        var old = Lines(before);
        var now = Lines(after);
        var removed = old.Except(now).Count();
        var added = now.Except(old).Count();
        return string.Create(CultureInfo.InvariantCulture, $"{added} lines added, {removed} removed, {now.Count} lines now");
    }

    private static List<string> Lines(string text) => [.. text.ReplaceLineEndings("\n").Split('\n')];

    private async Task<string> ApprovedTextAsync(PackageScriptEntity script, CancellationToken cancellationToken) =>
        script.ApprovedRevision is { } revision && await db.PackageScriptRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.ScriptId == script.Id && r.Revision == revision, cancellationToken).ConfigureAwait(false) is { } approved
            ? ScriptSigningFormat.ContentText(approved.Content)
            : string.Empty;

    /// <summary>
    /// Approves the draft: scans it, signs it with the script key and stores it as the next revision, which agents run
    /// from their next release list. Refused without "I have read the whole script" or when the scan finds something.
    /// </summary>
    public async Task<Outcome> ApproveAsync(int scriptId, bool readWholeScript, string actor, CancellationToken cancellationToken)
    {
        var script = await db.PackageScripts.SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken).ConfigureAwait(false);
        if (script?.DraftContent is not { } text)
        {
            return new Outcome("There is no draft to approve.");
        }

        if (!readWholeScript)
        {
            return new Outcome("Confirm that you have read the whole script.");
        }

        var parameters = ScriptSigningFormat.ReadParameters(script.DraftParametersJson ?? "[]");
        if (Validate(script.Phase, text, parameters, script.DraftRunOn, script.DraftOnFailure, script.DraftTimeoutSeconds) is { } error)
        {
            return new Outcome(error);
        }

        var content = ScriptSigningFormat.NormalizeContent(text);
        var (state, detail) = Scan(content, $"{script.PackageId}-{script.Phase}.ps1");
        if (softwareOptions.Value.ScanMode == ScanMode.Enforce && state is InstallerScanState.Detected or InstallerScanState.Failed)
        {
            await audit.WriteAsync(actor, "Script.ScanRefused", $"{script.PackageId} {script.Phase}", detail, cancellationToken).ConfigureAwait(false);
            return new Outcome($"The virus scan refused the script: {detail}");
        }

        var (key, ecdsa) = await signing.ActiveAsync(actor, cancellationToken).ConfigureAwait(false);
        var revision = (await db.PackageScriptRevisions.Where(r => r.ScriptId == script.Id).MaxAsync(r => (int?)r.Revision, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        now = DateTime.SpecifyKind(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Utc);
        var parametersJson = script.DraftParametersJson ?? "[]";
        var contentHash = ScriptSigningFormat.Sha256(content);
        var parametersHash = ScriptSigningFormat.ParametersSha256(parametersJson);
        var signed = ScriptSigningFormat.RevisionBytes(script.Id, revision, script.PackageId, script.Phase, script.DraftRunOn, script.DraftOnFailure, script.DraftTimeoutSeconds, contentHash, parametersHash, new DateTimeOffset(now), script.DraftPowerShell);
        db.PackageScriptRevisions.Add(new PackageScriptRevisionEntity
        {
            ScriptId = script.Id,
            Revision = revision,
            Content = content,
            ContentSha256 = contentHash,
            ParametersJson = parametersJson,
            ParametersSha256 = parametersHash,
            RunOn = script.DraftRunOn,
            OnFailure = script.DraftOnFailure,
            TimeoutSeconds = script.DraftTimeoutSeconds,
            PowerShell = script.DraftPowerShell,
            KeyId = key.KeyId,
            Signature = ScriptSigning.Sign(ecdsa, signed),
            SignedAtUtc = now,
            ApprovedBy = actor,
            ScanState = state,
            ScanDetail = detail is { Length: > 512 } longDetail ? longDetail[..512] : detail,
        });
        script.ApprovedRevision = revision;
        script.DraftContent = null;
        script.DraftParametersJson = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AnnounceAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Script.Approved", $"{script.PackageId} {script.Phase} revision {revision}", $"SHA-256 {contentHash}, key {key.KeyId}, scan {state}", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private (InstallerScanState State, string? Detail) Scan(byte[] content, string name)
    {
        if (softwareOptions.Value.ScanMode == ScanMode.Off)
        {
            return (InstallerScanState.NotScanned, null);
        }

        var result = scanner.ScanBuffer(content, name);
        return result.Outcome switch
        {
            ScanOutcome.Detected => (InstallerScanState.Detected, result.Detail),
            ScanOutcome.Failed or ScanOutcome.TooLarge => (InstallerScanState.Failed, result.Detail),
            ScanOutcome.Unavailable => (InstallerScanState.NotScanned, result.Detail),
            _ => (InstallerScanState.Clean, null),
        };
    }

    /// <summary>Throws the draft away; with no approved revision the script goes.</summary>
    public async Task<Outcome> DiscardDraftAsync(int scriptId, string actor, CancellationToken cancellationToken)
    {
        var script = await db.PackageScripts.SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken).ConfigureAwait(false);
        if (script is null)
        {
            return new Outcome("The script no longer exists.");
        }

        if (script.ApprovedRevision is null)
        {
            db.PackageScripts.Remove(script);
        }
        else
        {
            script.DraftContent = null;
            script.DraftParametersJson = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Script.DraftDiscarded", $"{script.PackageId} {script.Phase}", null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Deletes a script with its revisions and values; from the next release list on no agent runs it.</summary>
    public async Task<Outcome> DeleteAsync(int scriptId, string actor, CancellationToken cancellationToken)
    {
        var script = await db.PackageScripts.SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken).ConfigureAwait(false);
        if (script is null)
        {
            return new Outcome("The script no longer exists.");
        }

        await db.PackageAssignmentScriptValues.Where(v => v.ScriptId == scriptId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.PackageScriptRevisions.Where(r => r.ScriptId == scriptId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.PackageScripts.Remove(script);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AnnounceAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Script.Deleted", $"{script.PackageId} {script.Phase}", script.ApprovedRevision is { } revision ? $"last revision {revision}" : "draft only", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>
    /// Sets the values of an assignment for the parameters of the approved scripts of its package. An empty secret keeps the
    /// stored value; values are audited by name only.
    /// </summary>
    public async Task<Outcome> SetValuesAsync(int assignmentId, IReadOnlyDictionary<(int ScriptId, string Name), string?> values, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var assignment = await db.PackageAssignments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assignmentId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return new Outcome("The assignment no longer exists.");
        }

        var scripts = await ListAsync(assignment.PackageId, cancellationToken).ConfigureAwait(false);
        var stored = await db.PackageAssignmentScriptValues.Where(v => v.AssignmentId == assignmentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changed = new List<string>();
        foreach (var ((scriptId, name), value) in values)
        {
            var parameter = scripts.FirstOrDefault(s => s.Script.Id == scriptId)?.Approved is { } approved
                ? ScriptSigningFormat.ReadParameters(approved.ParametersJson).FirstOrDefault(p => p.Name == name)
                : null;
            if (parameter is null)
            {
                return new Outcome($"The script has no parameter {name}.");
            }

            if ((value?.Length ?? 0) > ScriptLimits.MaxValueLength || value?.Contains('\0', StringComparison.Ordinal) == true)
            {
                return new Outcome($"The value of {name} is too long or invalid.");
            }

            var existing = stored.FirstOrDefault(v => v.ScriptId == scriptId && v.Name == name);
            if (parameter.Secret && string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (existing is null)
            {
                existing = new PackageAssignmentScriptValueEntity { AssignmentId = assignmentId, ScriptId = scriptId, Name = name, ChangedBy = actor };
                db.PackageAssignmentScriptValues.Add(existing);
            }

            existing.Value = parameter.Secret ? null : (string.IsNullOrEmpty(value) ? null : value);
            existing.ProtectedValue = parameter.Secret ? ProtectedData.Protect(Encoding.UTF8.GetBytes(value!), SecretEntropy, DataProtectionScope.CurrentUser) : null;
            existing.ChangedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            existing.ChangedBy = actor;
            changed.Add(name);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (changed.Count > 0)
        {
            await AnnounceAsync(cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(actor, "Script.ValuesChanged", assignment.PackageId, $"assignment {assignmentId}: {string.Join(", ", changed)}", cancellationToken).ConfigureAwait(false);
        }

        return Outcome.Ok;
    }

    /// <summary>
    /// The approved scripts of a package an agent runs, for the given phases, with the parameter values for this server:
    /// those of its own assignment, else of an assignment of one of its groups, else the defaults. Empty while scripts are
    /// switched off.
    /// </summary>
    public async Task<IReadOnlyList<ScriptReference>> ReferencesAsync(string packageId, Guid clientId, IReadOnlyCollection<ScriptPhase> phases, CancellationToken cancellationToken)
    {
        if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var scripts = (await ListAsync(packageId, cancellationToken).ConfigureAwait(false)).Where(s => s.Approved is not null && phases.Contains(s.Script.Phase)).ToList();
        if (scripts.Count == 0)
        {
            return [];
        }

        var groups = await db.ClientGroups.AsNoTracking().Where(g => g.ClientId == clientId).Select(g => g.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var assignment = await db.PackageAssignments.AsNoTracking()
            .Where(a => a.PackageId == packageId && (a.ClientId == clientId || (a.GroupId != null && groups.Contains(a.GroupId.Value))))
            .OrderBy(a => a.ClientId == null).ThenBy(a => a.Id)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var values = assignment is { } id
            ? await db.PackageAssignmentScriptValues.AsNoTracking().Where(v => v.AssignmentId == id).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];
        return [.. scripts.Select(s =>
        {
            var parameters = ScriptSigningFormat.ReadParameters(s.Approved!.ParametersJson);
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                var stored = values.FirstOrDefault(v => v.ScriptId == s.Script.Id && v.Name == parameter.Name);
                var value = stored?.ProtectedValue is { } secret ? Unprotect(secret) : stored?.Value ?? parameter.Default;
                if (value is not null)
                {
                    resolved[parameter.Name] = value;
                }
            }

            return new ScriptReference(s.Script.Phase, s.Script.Id, s.Approved.Revision, s.Approved.ContentSha256, s.Approved.ParametersSha256, resolved);
        })];
    }

    private static string? Unprotect(byte[] secret)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(secret, SecretEntropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Restored on another machine: the secret has to be entered again; the run then misses it and reports why.
            return null;
        }
    }

    /// <summary>The package an agent downloads for an approved revision, with its signatures; null when unknown.</summary>
    public async Task<ScriptPackage?> PackageAsync(int scriptId, int revision, CancellationToken cancellationToken)
    {
        var row = await (from r in db.PackageScriptRevisions.AsNoTracking()
                         join s in db.PackageScripts.AsNoTracking() on r.ScriptId equals s.Id
                         join k in db.ScriptKeys.AsNoTracking() on r.KeyId equals k.KeyId
                         where r.ScriptId == scriptId && r.Revision == revision
                         select new { r, s.PackageId, s.Phase, k }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : new ScriptPackage
        {
            ScriptId = scriptId,
            Revision = revision,
            PackageId = row.PackageId,
            Phase = row.Phase,
            RunOn = row.r.RunOn,
            OnFailure = row.r.OnFailure,
            TimeoutSeconds = row.r.TimeoutSeconds,
            PowerShell = row.r.PowerShell,
            Content = Convert.ToBase64String(row.r.Content),
            ParametersJson = row.r.ParametersJson,
            SignedAt = new DateTimeOffset(DateTime.SpecifyKind(row.r.SignedAtUtc, DateTimeKind.Utc)),
            Signature = Convert.ToBase64String(row.r.Signature),
            KeyId = row.k.KeyId,
            KeyPublicKey = Convert.ToBase64String(row.k.PublicKey),
            KeyCreatedAt = new DateTimeOffset(DateTime.SpecifyKind(row.k.CreatedAtUtc, DateTimeKind.Utc)),
            KeyCertificate = Convert.ToBase64String(row.k.Certificate),
        };
    }
}
