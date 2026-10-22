using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>
/// Manual interventions in rollouts (start, stop, override, gate approval), start rules and transition rules.
/// Every change is validated, written to the audit log and followed by a release synchronization.
/// </summary>
internal sealed partial class RolloutAdministration(ServerDbContext db, RolloutService rollouts, ReleasePlanner releases, AuditLog audit, TimeProvider timeProvider)
{
    /// <summary>Starts the rollout of a queued update, or resumes a stopped one.</summary>
    public async Task<Outcome> StartAsync(Guid updateId, int revision, string actor, CancellationToken cancellationToken)
    {
        var update = await FindUpdateAsync(updateId, revision, cancellationToken).ConfigureAwait(false);
        if (update is null)
        {
            return new Outcome("The update is not in the catalog.");
        }

        if (update.BlockedAtUtc is not null)
        {
            return new Outcome(BlockedMessage);
        }

        var rollout = await db.Rollouts.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision, cancellationToken).ConfigureAwait(false);
        string action;
        if (rollout is null)
        {
            var created = await rollouts.CreateAsync(updateId, revision, actor, TimeSpan.Zero, null, cancellationToken).ConfigureAwait(false);

            // A manual start keeps the permission of the rule that would decide for the update (e.g. definition updates at any time).
            var rules = await db.StartRules.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            created.OutsideWindows = RolloutService.FindStartRule(rules, update)?.OutsideWindows == true;
            action = "Rollout.Started";
        }
        else if (rollout.State == RolloutState.Stopped)
        {
            rollout.State = RolloutState.Active;
            action = "Rollout.Resumed";
        }
        else
        {
            return new Outcome("The rollout is already running.");
        }

        return await CompleteAsync(actor, action, update, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops a rollout: its releases are withdrawn from every server that has not installed it yet.</summary>
    public async Task<Outcome> StopAsync(Guid updateId, int revision, string actor, CancellationToken cancellationToken)
    {
        var rollout = await db.Rollouts.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision, cancellationToken).ConfigureAwait(false);
        if (rollout is not { State: RolloutState.Active })
        {
            return new Outcome("The rollout is not running.");
        }

        rollout.State = RolloutState.Stopped;
        var update = await FindUpdateAsync(updateId, revision, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "Rollout.Stopped", update!, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Manual override: releases the update to every ring at once, bypassing rates, wait times and gates
    /// (e.g. a critical security update). Agents still install only inside their maintenance windows.
    /// </summary>
    public async Task<Outcome> OverrideAsync(Guid updateId, int revision, string actor, CancellationToken cancellationToken)
    {
        var update = await FindUpdateAsync(updateId, revision, cancellationToken).ConfigureAwait(false);
        if (update is null)
        {
            return new Outcome("The update is not in the catalog.");
        }

        if (update.BlockedAtUtc is not null)
        {
            return new Outcome(BlockedMessage);
        }

        var rollout = await db.Rollouts.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision, cancellationToken).ConfigureAwait(false)
            ?? await rollouts.CreateAsync(updateId, revision, actor, TimeSpan.Zero, null, cancellationToken).ConfigureAwait(false);
        if (rollout.Override && rollout.State == RolloutState.Active)
        {
            return new Outcome("The update is already released to every ring.");
        }

        rollout.Override = true;
        rollout.State = RolloutState.Active;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var ring in await db.RolloutRings.Where(r => r.UpdateId == updateId && r.Revision == revision && r.State != RingRolloutState.Released).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            ring.State = RingRolloutState.Released;
            ring.ReleasedAtUtc = now;
            ring.StateChangedAtUtc = now;
            ring.Reason = null;
        }

        return await CompleteAsync(actor, "Rollout.Override", update, "released to every ring", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Approves a ring that waits at a manual gate or after an alarm.</summary>
    public async Task<Outcome> ApproveRingAsync(Guid updateId, int revision, int ringId, string actor, CancellationToken cancellationToken)
    {
        if (await db.Updates.AnyAsync(u => u.UpdateId == updateId && u.Revision == revision && u.BlockedAtUtc != null, cancellationToken).ConfigureAwait(false))
        {
            return new Outcome(BlockedMessage);
        }

        var rollout = await db.Rollouts.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision, cancellationToken).ConfigureAwait(false);
        if (rollout is not { State: RolloutState.Active })
        {
            return new Outcome("The rollout is not running.");
        }

        var ring = await db.RolloutRings.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision && r.RingId == ringId, cancellationToken).ConfigureAwait(false);
        if (ring is not { State: RingRolloutState.AwaitingApproval or RingRolloutState.Alarm })
        {
            return new Outcome("The ring is not waiting for an approval.");
        }

        var details = ring.State == RingRolloutState.Alarm ? $"released despite alarm: {ring.Reason}" : "gate approved";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        ring.State = RingRolloutState.Released;
        ring.ReleasedAtUtc = now;
        ring.StateChangedAtUtc = now;
        ring.Reason = null;

        var ringName = await db.Rings.Where(r => r.Id == ringId).Select(r => r.Name).SingleAsync(cancellationToken).ConfigureAwait(false);
        var update = await FindUpdateAsync(updateId, revision, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(actor, "Rollout.RingApproved", update!, $"ring {ringName}, {details}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome> SetTransitionAsync(
        int fromRingId, int minimumWaitDays, int maximumWaitDays, int minimumFeedbackPercent, int minimumSuccessPercent, bool manualGate, string actor, CancellationToken cancellationToken)
    {
        var transition = await db.RingTransitions.SingleOrDefaultAsync(t => t.FromRingId == fromRingId, cancellationToken).ConfigureAwait(false);
        if (transition is null)
        {
            return new Outcome("The ring transition does not exist.");
        }

        if (minimumWaitDays < 0 || maximumWaitDays > RingTransitionEntity.MaxWaitDays || minimumWaitDays > maximumWaitDays)
        {
            return new Outcome($"Wait times must be between 0 and {RingTransitionEntity.MaxWaitDays} days, the minimum not above the maximum.");
        }

        if (minimumFeedbackPercent is < 0 or > 100 || minimumSuccessPercent is < 0 or > 100)
        {
            return new Outcome("Rates must be between 0 and 100 percent.");
        }

        transition.MinimumWait = TimeSpan.FromDays(minimumWaitDays);
        transition.MaximumWait = TimeSpan.FromDays(maximumWaitDays);
        transition.MinimumFeedbackPercent = minimumFeedbackPercent;
        transition.MinimumSuccessPercent = minimumSuccessPercent;
        transition.ManualGate = manualGate;

        var ringName = await db.Rings.Where(r => r.Id == fromRingId).Select(r => r.Name).SingleAsync(cancellationToken).ConfigureAwait(false);
        var details = string.Create(CultureInfo.InvariantCulture, $"wait {minimumWaitDays}-{maximumWaitDays} days, feedback {minimumFeedbackPercent}%, success {minimumSuccessPercent}%, manual gate {(manualGate ? "on" : "off")}");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "RingTransition.Changed", $"from {ringName}", details, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>Creates a start rule; conditions are optional, a rule without conditions matches every update.</summary>
    public Task<Outcome> CreateStartRuleAsync(
        int priority, string? classification, string? msrcSeverity, string? titlePattern, StartMode mode, int delayHours, string actor, CancellationToken cancellationToken,
        IReadOnlyCollection<int>? startRings = null, RebootBehavior? rebootBehavior = null, bool outsideWindows = false) =>
        SaveStartRuleAsync(null, priority, classification, msrcSeverity, titlePattern, mode, delayHours, startRings ?? [], rebootBehavior, outsideWindows, actor, cancellationToken);

    public Task<Outcome> UpdateStartRuleAsync(
        int ruleId, int priority, string? classification, string? msrcSeverity, string? titlePattern, StartMode mode, int delayHours, IReadOnlyCollection<int> startRings, string actor, CancellationToken cancellationToken,
        RebootBehavior? rebootBehavior = null, bool outsideWindows = false) =>
        SaveStartRuleAsync(ruleId, priority, classification, msrcSeverity, titlePattern, mode, delayHours, startRings, rebootBehavior, outsideWindows, actor, cancellationToken);

    /// <summary>Classification Windows Update reports for antivirus definitions (Defender and others).</summary>
    public const string DefinitionUpdates = "Definition Updates";

    /// <summary>
    /// Creates the default start rule for definition updates once (Marc, 2026-10-21): they never restart, so they start in
    /// every ring at once and install outside the maintenance windows. Administrators may change or delete it; it is not
    /// created again. Skipped when a rule for definition updates already exists.
    /// </summary>
    public async Task<bool> EnsureDefinitionRuleAsync(CancellationToken cancellationToken)
    {
        if (await db.Settings.AnyAsync(s => s.Key == SettingEntity.DefinitionRuleCreated, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        db.Settings.Add(new SettingEntity { Key = SettingEntity.DefinitionRuleCreated, Value = "true" });
        var rules = await db.StartRules.AsNoTracking().Select(r => new { r.Id, r.Priority, r.Classification }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rules.Any(r => string.Equals(r.Classification, DefinitionUpdates, StringComparison.OrdinalIgnoreCase)))
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var rings = await db.Rings.AsNoTracking().OrderBy(r => r.Order).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rule = new StartRuleEntity
        {
            Priority = NextFreePriority([.. rules.Select(r => (r.Id, r.Priority))]),
            Classification = DefinitionUpdates,
            RebootBehavior = (int)RebootBehavior.NeverReboots,
            Mode = StartMode.Automatic,
            Delay = TimeSpan.Zero,
            StartRingIds = StartRings.Format(rings.Skip(1)),
            OutsideWindows = true,
        };
        db.StartRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync("system", "StartRule.Created", rule.Mode.ToString(), string.Create(CultureInfo.InvariantCulture, $"default rule for definition updates: priority {rule.Priority}, never restarts, all rings, outside windows"), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Priority suggested for a new start rule: the first free one after the highest rule (from 100 on), below the catch-all rule where possible.</summary>
    public static int NextFreePriority(IReadOnlyCollection<(int Id, int Priority)> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var catchAll = rules.Where(r => r.Id == StartRuleEntity.CatchAllId).Select(r => (int?)r.Priority).FirstOrDefault();
        return Administration.RulePriority.NextFree([.. rules.Where(r => r.Id != StartRuleEntity.CatchAllId).Select(r => r.Priority)], catchAll);
    }

    /// <summary>Validates and stores a start rule; <c>startRings</c> lists rings released at once besides the first ring.</summary>
    private async Task<Outcome> SaveStartRuleAsync(
        int? ruleId, int priority, string? classification, string? msrcSeverity, string? titlePattern, StartMode mode, int delayHours, IReadOnlyCollection<int> startRings,
        RebootBehavior? rebootBehavior, bool outsideWindows, string actor, CancellationToken cancellationToken)
    {
        if (rebootBehavior is { } behavior && !Enum.IsDefined(behavior))
        {
            return new Outcome("The restart condition is invalid.");
        }

        // Only updates that never restart may skip the maintenance window; a restart always waits for one.
        if (outsideWindows && rebootBehavior != RebootBehavior.NeverReboots)
        {
            return new Outcome("Installing outside the maintenance windows needs the restart condition \"No restart\".");
        }

        classification = Normalize(classification);
        msrcSeverity = Normalize(msrcSeverity);
        titlePattern = Normalize(titlePattern);

        if (priority is < 0 or > 10_000)
        {
            return new Outcome("The priority must be between 0 and 10000.");
        }

        if (!Enum.IsDefined(mode))
        {
            return new Outcome("The start mode is invalid.");
        }

        if (delayHours < 0 || delayHours > StartRuleEntity.MaxDelayHours || (mode == StartMode.Manual && delayHours != 0))
        {
            return new Outcome($"The delay must be between 0 and {StartRuleEntity.MaxDelayHours} hours, and 0 for a manual start.");
        }

        if (classification is not null && !LabelRegex().IsMatch(classification))
        {
            return new Outcome("The classification may contain letters, digits, spaces, '-' and '.' (at most 64 characters).");
        }

        if (msrcSeverity is not null && !SeverityRegex().IsMatch(msrcSeverity))
        {
            return new Outcome("The severity must be a single word, e.g. Critical.");
        }

        if (titlePattern is not null && (titlePattern.Length > StartRuleEntity.MaxTitlePatternLength || titlePattern.Any(char.IsControl)))
        {
            return new Outcome("The title pattern may have at most 128 characters and no control characters.");
        }

        // Two rules with the same priority would decide by creation order, which nobody sees in the list.
        if (await db.StartRules.AnyAsync(r => r.Priority == priority && (ruleId == null || r.Id != ruleId), cancellationToken).ConfigureAwait(false))
        {
            var rules = await db.StartRules.AsNoTracking().Select(r => new { r.Id, r.Priority }).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new Outcome(string.Create(CultureInfo.InvariantCulture, $"Another start rule already has priority {priority}; choose a free one, e.g. {NextFreePriority([.. rules.Where(r => r.Id != ruleId).Select(r => (r.Id, r.Priority))])}."));
        }

        var rings = await db.Rings.AsNoTracking().OrderBy(r => r.Order).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (startRings.Any(id => !rings.Contains(id)))
        {
            return new Outcome("A selected ring does not exist.");
        }

        StartRuleEntity rule;
        if (ruleId is { } id)
        {
            var existing = await db.StartRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return new Outcome("The rule no longer exists.");
            }

            rule = existing;
        }
        else
        {
            rule = new StartRuleEntity();
            db.StartRules.Add(rule);
        }

        rule.Priority = priority;
        rule.Classification = classification;
        rule.MsrcSeverity = msrcSeverity;
        rule.TitlePattern = titlePattern;
        rule.Mode = mode;
        rule.Delay = TimeSpan.FromHours(delayHours);
        rule.RebootBehavior = (int?)rebootBehavior;
        rule.OutsideWindows = outsideWindows;

        // The first ring always starts; storing it would only repeat the default.
        rule.StartRingIds = StartRings.Format(startRings.Where(r => r != rings[0]));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = string.Create(CultureInfo.InvariantCulture, $"priority {priority}, classification {classification ?? "-"}, severity {msrcSeverity ?? "-"}, title {titlePattern ?? "-"}, {mode}, delay {delayHours} hours, also starts in rings {rule.StartRingIds ?? "-"}, restart {rebootBehavior?.ToString() ?? "-"}, outside windows {(outsideWindows ? "yes" : "no")}");
        await audit.WriteAsync(actor, ruleId is null ? "StartRule.Created" : "StartRule.Changed", mode.ToString(), details, cancellationToken).ConfigureAwait(false);

        // A new or changed rule also applies to updates that were reported before it existed.
        await rollouts.AutoStartWaitingAsync(cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    public async Task<Outcome> DeleteStartRuleAsync(int ruleId, string actor, CancellationToken cancellationToken)
    {
        var deleted = await db.StartRules.Where(r => r.Id == ruleId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (deleted == 0)
        {
            return new Outcome("The rule no longer exists.");
        }

        await audit.WriteAsync(actor, "StartRule.Deleted", ruleId.ToString(CultureInfo.InvariantCulture), null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private const string BlockedMessage = "The update is blocked; unblock it first.";

    /// <summary>True if the rollout of an update that looks withdrawn by Microsoft is stopped (default); false to only notify.</summary>
    public async Task<bool> GetStopWithdrawnAsync(CancellationToken cancellationToken) =>
        await db.Settings.AsNoTracking().Where(s => s.Key == SettingEntity.WithdrawnAction).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) != "notify";

    public async Task<Outcome> SetStopWithdrawnAsync(bool stop, string actor, CancellationToken cancellationToken)
    {
        var value = stop ? "stop" : "notify";
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingEntity.WithdrawnAction, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingEntity.WithdrawnAction, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Rollout.WithdrawnActionChanged", value, null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    /// <summary>
    /// Blocks an update: a running rollout stops (its releases are withdrawn from every server that has not installed it)
    /// and neither start rules nor operators start it again until it is unblocked. Also for queued updates.
    /// </summary>
    public async Task<Outcome> BlockAsync(Guid updateId, int revision, string actor, CancellationToken cancellationToken)
    {
        var update = await db.Updates.SingleOrDefaultAsync(u => u.UpdateId == updateId && u.Revision == revision, cancellationToken).ConfigureAwait(false);
        if (update is null)
        {
            return new Outcome("The update is not in the catalog.");
        }

        if (update.BlockedAtUtc is not null)
        {
            return new Outcome("The update is already blocked.");
        }

        update.BlockedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        update.BlockedBy = actor.Length > 256 ? actor[..256] : actor;
        if (await db.Rollouts.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision, cancellationToken).ConfigureAwait(false) is { State: RolloutState.Active } rollout)
        {
            rollout.State = RolloutState.Stopped;
        }

        return await CompleteAsync(actor, "Update.Blocked", update, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lifts a block; a stopped rollout stays stopped until an operator resumes it.</summary>
    public async Task<Outcome> UnblockAsync(Guid updateId, int revision, string actor, CancellationToken cancellationToken)
    {
        var update = await db.Updates.SingleOrDefaultAsync(u => u.UpdateId == updateId && u.Revision == revision, cancellationToken).ConfigureAwait(false);
        if (update?.BlockedAtUtc is null)
        {
            return new Outcome("The update is not blocked.");
        }

        update.BlockedAtUtc = null;
        update.BlockedBy = null;
        return await CompleteAsync(actor, "Update.Unblocked", update, null, cancellationToken).ConfigureAwait(false);
    }

    private Task<UpdateEntity?> FindUpdateAsync(Guid updateId, int revision, CancellationToken cancellationToken) =>
        db.Updates.AsNoTracking().SingleOrDefaultAsync(u => u.UpdateId == updateId && u.Revision == revision, cancellationToken);

    private async Task<Outcome> CompleteAsync(string actor, string action, UpdateEntity update, string? details, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, action, RolloutService.Describe(update.KbArticle, update.Title, update.UpdateId, update.Revision), details, cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 .\-]{0,63}$")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^[A-Za-z]{1,32}$")]
    private static partial Regex SeverityRegex();
}

/// <summary>Rollout actions posted by dashboard forms; shared by the update list and the update detail page.</summary>
internal static class RolloutActions
{
    /// <summary>Runs "start", "stop", "override", "block", "unblock" or "approve" (with a ring) and returns the outcome and the success message.</summary>
    public static async Task<(Outcome Outcome, string Success)> RunAsync(
        RolloutAdministration administration, string? action, Guid updateId, int revision, int? ringId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);
        return action switch
        {
            "start" => (await administration.StartAsync(updateId, revision, actor, cancellationToken).ConfigureAwait(false), "The rollout runs."),
            "stop" => (await administration.StopAsync(updateId, revision, actor, cancellationToken).ConfigureAwait(false), "The rollout was stopped; servers that have not installed the update no longer get it."),
            "override" => (await administration.OverrideAsync(updateId, revision, actor, cancellationToken).ConfigureAwait(false), "The update is released to every ring."),
            "block" => (await administration.BlockAsync(updateId, revision, actor, cancellationToken).ConfigureAwait(false), "The update is blocked: its rollout is stopped and it does not start again until it is unblocked."),
            "unblock" => (await administration.UnblockAsync(updateId, revision, actor, cancellationToken).ConfigureAwait(false), "The update is unblocked; resume or start its rollout when it should be installed again."),
            "approve" when ringId is { } ring => (await administration.ApproveRingAsync(updateId, revision, ring, actor, cancellationToken).ConfigureAwait(false), "The update was released for the ring."),
            _ => (new Outcome("Invalid request."), string.Empty),
        };
    }
}
