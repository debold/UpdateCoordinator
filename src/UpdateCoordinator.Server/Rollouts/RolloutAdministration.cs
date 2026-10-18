using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Server.Administration;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Rollouts;

/// <summary>
/// Manual interventions in rollouts (start, stop, override, gate approval) and the release rules.
/// Every change is validated, written to the audit log and followed by a release synchronization.
/// </summary>
internal sealed partial class RolloutAdministration(ServerDbContext db, RolloutService rollouts, ReleasePlanner releases, AuditLog audit, TimeProvider timeProvider)
{
    public const int MaxClassifications = 20;

    /// <summary>Starts the rollout of a queued update, or resumes a stopped one.</summary>
    public async Task<Outcome> StartAsync(Guid updateId, int revision, string actor, CancellationToken cancellationToken)
    {
        var update = await FindUpdateAsync(updateId, revision, cancellationToken).ConfigureAwait(false);
        if (update is null)
        {
            return new Outcome("The update is not in the catalog.");
        }

        var rollout = await db.Rollouts.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision, cancellationToken).ConfigureAwait(false);
        string action;
        if (rollout is null)
        {
            await rollouts.CreateAsync(updateId, revision, actor, cancellationToken).ConfigureAwait(false);
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

        var rollout = await db.Rollouts.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision, cancellationToken).ConfigureAwait(false)
            ?? await rollouts.CreateAsync(updateId, revision, actor, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Sets the classifications whose rollouts start automatically; one per line, empty disables automatic starts.</summary>
    public async Task<Outcome> SetAutoStartClassificationsAsync(string? text, string actor, CancellationToken cancellationToken)
    {
        var classifications = (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (classifications.Count > MaxClassifications)
        {
            return new Outcome($"At most {MaxClassifications} classifications are allowed.");
        }

        var invalid = classifications.FirstOrDefault(c => !ClassificationRegex().IsMatch(c));
        if (invalid is not null)
        {
            return new Outcome("A classification may contain letters, digits, spaces, '-' and '.' (at most 64 characters).");
        }

        var json = JsonSerializer.Serialize(classifications);
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingEntity.AutoStartClassifications, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            db.Settings.Add(new SettingEntity { Key = SettingEntity.AutoStartClassifications, Value = json });
        }
        else
        {
            setting.Value = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(actor, "Rollout.AutoStartChanged", "classifications", classifications.Count > 0 ? string.Join(", ", classifications) : "none", cancellationToken).ConfigureAwait(false);
        return Outcome.Ok;
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
    private static partial Regex ClassificationRegex();
}
