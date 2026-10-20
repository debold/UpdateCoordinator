using System.Globalization;

using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Administration;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>
/// Ring logic (docs/04-central-server.md, section 3): starts rollouts, moves each update from ring to ring according to
/// the transition rules and derives the releases of the clients.
/// </summary>
internal sealed partial class RolloutService(ServerDbContext db, ReleasePlanner releases, AuditLog audit, TimeProvider timeProvider, ILogger<RolloutService> logger)
{
    public const string RuleActor = "rule";

    /// <summary>
    /// Creates the rollout of an update: released in the first ring, waiting in all others. Does not save.
    /// With a delay, servers of the first ring install it at the earliest after the delay, and the transition wait time starts then.
    /// </summary>
    /// <param name="updateId">The update.</param>
    /// <param name="revision">Its revision.</param>
    /// <param name="actor">Who started it.</param>
    /// <param name="delay">Delay before the first installation.</param>
    /// <param name="startRings">Further rings released at once (start rule); unknown IDs are ignored.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<RolloutEntity> CreateAsync(Guid updateId, int revision, string actor, TimeSpan delay, IReadOnlyCollection<int>? startRings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rings = await db.Rings.AsNoTracking().OrderBy(r => r.Order).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        bool StartsHere(int ring, int index) => index == 0 || startRings?.Contains(ring) == true;
        var rollout = new RolloutEntity { UpdateId = updateId, Revision = revision, State = RolloutState.Active, StartedAtUtc = now, StartedBy = actor };
        db.Rollouts.Add(rollout);
        db.RolloutRings.AddRange(rings.Select((ring, index) => new RolloutRingEntity
        {
            UpdateId = updateId,
            Revision = revision,
            RingId = ring,
            State = StartsHere(ring, index) ? RingRolloutState.Released : RingRolloutState.Waiting,
            ReleasedAtUtc = StartsHere(ring, index) ? now + delay : null,
            StateChangedAtUtc = now,
        }));
        return rollout;
    }

    /// <summary>The start rule for an update: the matching rule with the lowest priority number, or null.</summary>
    internal static StartRuleEntity? FindStartRule(IEnumerable<StartRuleEntity> rules, UpdateEntity update) => rules
        .OrderBy(r => r.Priority)
        .ThenBy(r => r.Id)
        .FirstOrDefault(r =>
            (r.Classification is null || string.Equals(r.Classification, update.Classification, StringComparison.OrdinalIgnoreCase))
            && (r.MsrcSeverity is null || string.Equals(r.MsrcSeverity, update.MsrcSeverity, StringComparison.OrdinalIgnoreCase))
            && (r.TitlePattern is null || Wildcard.Matches(r.TitlePattern, update.Title)));

    /// <summary>Starts rollouts for newly reported updates according to the start rules.</summary>
    public async Task AutoStartAsync(IReadOnlyCollection<(Guid UpdateId, int Revision)> updates, CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var ids = updates.Select(u => u.UpdateId).Distinct().ToList();
        var candidates = await db.Updates.AsNoTracking()
            .Where(u => ids.Contains(u.UpdateId))
            .Where(u => !db.Rollouts.Any(r => r.UpdateId == u.UpdateId && r.Revision == u.Revision))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await StartMatchingAsync([.. candidates.Where(c => updates.Contains((c.UpdateId, c.Revision)))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the start rules to every waiting update (no rollout yet, not archived, needed by an approved server), so a new
    /// or changed rule also catches updates reported before it existed. Runs after a rule is saved and with the ring logic.
    /// </summary>
    public async Task<int> AutoStartWaitingAsync(CancellationToken cancellationToken)
    {
        var approved = db.Clients.Where(c => c.State == RegistrationState.Approved).Select(c => c.Id);
        var candidates = await db.Updates.AsNoTracking()
            .Where(u => u.ArchivedAtUtc == null)
            .Where(u => !db.Rollouts.Any(r => r.UpdateId == u.UpdateId && r.Revision == u.Revision))
            .Where(u => db.ClientAvailableUpdates.Any(a => a.UpdateId == u.UpdateId && a.Revision == u.Revision && approved.Contains(a.ClientId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var started = await StartMatchingAsync(candidates, cancellationToken).ConfigureAwait(false);
        if (started > 0)
        {
            await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        }

        return started;
    }

    private async Task<int> StartMatchingAsync(IReadOnlyList<UpdateEntity> candidates, CancellationToken cancellationToken)
    {
        var rules = await db.StartRules.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var started = 0;
        foreach (var update in candidates)
        {
            if (FindStartRule(rules, update) is not { Mode: StartMode.Automatic } rule)
            {
                continue;
            }

            await CreateAsync(update.UpdateId, update.Revision, RuleActor, rule.Delay, StartRings.Parse(rule.StartRingIds), cancellationToken).ConfigureAwait(false);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Started concurrently by another report; that rollout stays.
                db.ChangeTracker.Clear();
                continue;
            }

            var details = string.Create(CultureInfo.InvariantCulture, $"automatic start by start rule {rule.Id}, delay {rule.Delay.TotalHours:0} hours");
            await audit.WriteAsync(RuleActor, "Rollout.Started", Describe(update.KbArticle, update.Title, update.UpdateId, update.Revision), details, cancellationToken).ConfigureAwait(false);
            LogAutoStarted(update.UpdateId, update.Revision, rule.Id, rule.Delay);
            started++;
        }

        return started;
    }

    /// <summary>
    /// Evaluates all transitions of active rollouts and synchronizes the releases. Returns the number of ring changes.
    /// </summary>
    public async Task<int> AdvanceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rings = await db.Rings.AsNoTracking().OrderBy(r => r.Order).ToListAsync(cancellationToken).ConfigureAwait(false);
        var transitions = await db.RingTransitions.AsNoTracking().ToDictionaryAsync(t => t.FromRingId, cancellationToken).ConfigureAwait(false);
        var active = await db.Rollouts.AsNoTracking()
            .Where(r => r.State == RolloutState.Active && !r.Override)
            .Select(r => new { r.UpdateId, r.Revision })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var changes = 0;
        foreach (var rollout in active)
        {
            var states = await db.RolloutRings
                .Where(r => r.UpdateId == rollout.UpdateId && r.Revision == rollout.Revision)
                .ToDictionaryAsync(r => r.RingId, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < rings.Count - 1; i++)
            {
                if (!states.TryGetValue(rings[i].Id, out var from) || !states.TryGetValue(rings[i + 1].Id, out var to)
                    || from.State != RingRolloutState.Released || to.State != RingRolloutState.Waiting
                    || !transitions.TryGetValue(rings[i].Id, out var rule))
                {
                    continue;
                }

                var statistics = await GetStatisticsAsync(rollout.UpdateId, rollout.Revision, rings[i].Id, cancellationToken).ConfigureAwait(false);
                var (decision, reason) = TransitionRule.Evaluate(rule, statistics, from.ReleasedAtUtc ?? now, now);
                if (decision == TransitionDecision.Wait)
                {
                    continue;
                }

                to.StateChangedAtUtc = now;
                to.Reason = reason;
                if (decision == TransitionDecision.Alarm)
                {
                    to.State = RingRolloutState.Alarm;
                    LogAlarm(rollout.UpdateId, rollout.Revision, rings[i + 1].Name, reason!);
                }
                else if (rule.ManualGate)
                {
                    to.State = RingRolloutState.AwaitingApproval;
                    LogAwaitingApproval(rollout.UpdateId, rollout.Revision, rings[i + 1].Name);
                }
                else
                {
                    to.State = RingRolloutState.Released;
                    to.ReleasedAtUtc = now;
                    LogReleased(rollout.UpdateId, rollout.Revision, rings[i + 1].Name);
                }

                changes++;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return changes;
    }

    /// <summary>Results of the servers currently in a ring for one update.</summary>
    public async Task<RingStatistics> GetStatisticsAsync(Guid updateId, int revision, int ringId, CancellationToken cancellationToken)
    {
        var ringClients = db.Clients.Where(c => c.RingId == ringId && c.State == RegistrationState.Approved).Select(c => c.Id);

        var releasedTo = await db.Releases
            .Where(r => r.UpdateId == updateId && r.Revision == revision && ringClients.Contains(r.ClientId))
            .Select(r => r.ClientId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var results = await db.Installations
            .Where(i => i.UpdateId == updateId && i.Revision == revision && ringClients.Contains(i.ClientId))
            .Select(i => new { i.ClientId, i.Id, i.ResultCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The last result per server counts, so a successful retry replaces an earlier failure.
        var last = results.GroupBy(r => r.ClientId).Select(g => g.MaxBy(r => r.Id)!).ToList();
        var targeted = releasedTo.Concat(last.Select(r => r.ClientId)).Distinct().Count();
        var succeeded = last.Count(r => r.ResultCode is (int)InstallResultCode.Succeeded or (int)InstallResultCode.SucceededWithErrors);
        return new RingStatistics(targeted, last.Count, succeeded);
    }

    internal static string Describe(string? kb, string title, Guid updateId, int revision) =>
        $"{UpdateText.Title(kb, title)} ({updateId:D}, revision {revision})";

    [LoggerMessage(Level = LogLevel.Information, Message = "Rollout of {UpdateId} revision {Revision} started automatically by start rule {RuleId} (delay {Delay}).")]
    private partial void LogAutoStarted(Guid updateId, int revision, int ruleId, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {UpdateId} revision {Revision} released for ring {Ring}.")]
    private partial void LogReleased(Guid updateId, int revision, string ring);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {UpdateId} revision {Revision} waits for manual approval before ring {Ring}.")]
    private partial void LogAwaitingApproval(Guid updateId, int revision, string ring);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update {UpdateId} revision {Revision} stopped before ring {Ring}: {Reason}")]
    private partial void LogAlarm(Guid updateId, int revision, string ring, string reason);
}
