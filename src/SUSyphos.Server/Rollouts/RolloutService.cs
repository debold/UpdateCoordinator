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

    /// <summary>"priority 101, delay 0 h, starts in Pilot, Test": what the rule did when it started a rollout.</summary>
    private async Task<string> DescribeRuleAsync(StartRuleEntity rule, CancellationToken cancellationToken)
    {
        var rings = await db.Rings.AsNoTracking().OrderBy(r => r.Order).Select(r => new { r.Id, r.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var startRings = StartRings.Parse(rule.StartRingIds);
        var names = rings.Where((r, index) => index == 0 || startRings.Contains(r.Id)).Select(r => r.Name);
        var outside = rule.OutsideWindows ? ", outside maintenance windows" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"priority {rule.Priority}, delay {rule.Delay.TotalHours:0} h, starts in {string.Join(", ", names)}{outside}");
    }

    /// <summary>The start rule for an update: the matching rule with the lowest priority number, or null.</summary>
    internal static StartRuleEntity? FindStartRule(IEnumerable<StartRuleEntity> rules, UpdateEntity update) => rules
        .OrderBy(r => r.Priority)
        .ThenBy(r => r.Id)
        .FirstOrDefault(r =>
            (r.Classification is null || string.Equals(r.Classification, update.Classification, StringComparison.OrdinalIgnoreCase))
            && (r.MsrcSeverity is null || string.Equals(r.MsrcSeverity, update.MsrcSeverity, StringComparison.OrdinalIgnoreCase))
            && (r.TitlePattern is null || Wildcard.Matches(r.TitlePattern, update.Title))
            && (r.RebootBehavior is null || r.RebootBehavior == update.RebootBehavior));

    /// <summary>Starts rollouts for newly reported updates according to the start rules.</summary>
    public async Task AutoStartAsync(IReadOnlyCollection<(Guid UpdateId, int Revision)> updates, CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var ids = updates.Select(u => u.UpdateId).Distinct().ToList();
        var candidates = await db.Updates.AsNoTracking()
            .Where(u => ids.Contains(u.UpdateId) && u.BlockedAtUtc == null)
            .Where(u => !db.UpdateSupersedence.Any(s => s.SupersededUpdateId == u.UpdateId && s.UpdateId != u.UpdateId))
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
            .Where(u => u.ArchivedAtUtc == null && u.BlockedAtUtc == null)

            // An update a known newer one replaces is not started: Windows Update offers the newer one instead.
            .Where(u => !db.UpdateSupersedence.Any(s => s.SupersededUpdateId == u.UpdateId && s.UpdateId != u.UpdateId))
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

            var rollout = await CreateAsync(update.UpdateId, update.Revision, RuleActor, rule.Delay, StartRings.Parse(rule.StartRingIds), cancellationToken).ConfigureAwait(false);
            rollout.StartRuleId = rule.Id;
            rollout.OutsideWindows = rule.OutsideWindows;
            rollout.StartRuleSummary = await DescribeRuleAsync(rule, cancellationToken).ConfigureAwait(false);
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

        // A ring on hold for an update does not count as done: the next ring waits, as at a manual gate.
        var heldRings = (await UpdateHolds.ActiveAsync(db, now, cancellationToken).ConfigureAwait(false))
            .Where(h => h.RingId is not null)
            .Select(h => (h.UpdateId, h.Revision, h.RingId!.Value))
            .ToHashSet();

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
                    || !transitions.TryGetValue(rings[i].Id, out var rule)
                    || heldRings.Contains((rollout.UpdateId, rollout.Revision, rings[i].Id)))
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

        changes += await StopSupersededAsync(cancellationToken).ConfigureAwait(false);
        changes += await MarkWithdrawnAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await releases.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        return changes;
    }

    /// <summary>
    /// Stops the rollout of a superseded update once an active rollout of an update that replaces it is released in every
    /// ring the older one reached (Marc, 2026-10-21). Until then each ring keeps the older update; the release planner
    /// already withdraws it per server where the newer one arrives. Does not save.
    /// </summary>
    private async Task<int> StopSupersededAsync(CancellationToken cancellationToken)
    {
        var active = await db.Rollouts.Where(r => r.State == RolloutState.Active).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = active.Select(r => r.UpdateId).Distinct().ToList();
        var pairs = await db.UpdateSupersedence.AsNoTracking()
            .Where(s => ids.Contains(s.SupersededUpdateId) && ids.Contains(s.UpdateId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (pairs.Count == 0)
        {
            return 0;
        }

        var activeKeys = active.Select(r => (r.UpdateId, r.Revision)).ToHashSet();
        var reached = (await db.RolloutRings.AsNoTracking()
                .Where(r => r.State == RingRolloutState.Released && ids.Contains(r.UpdateId))
                .Select(r => new { r.UpdateId, r.Revision, r.RingId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(r => (r.UpdateId, r.Revision), r => r.RingId);

        var stopped = 0;
        foreach (var old in active)
        {
            var oldRings = reached[(old.UpdateId, old.Revision)].ToHashSet();
            var newer = pairs
                .Where(p => p.SupersededUpdateId == old.UpdateId && activeKeys.Contains((p.UpdateId, p.Revision)) && p.UpdateId != old.UpdateId)
                .FirstOrDefault(p => oldRings.IsSubsetOf(reached[(p.UpdateId, p.Revision)]));
            if (oldRings.Count == 0 || newer is null)
            {
                continue;
            }

            old.State = RolloutState.Stopped;
            var updates = await db.Updates.AsNoTracking()
                .Where(u => (u.UpdateId == old.UpdateId && u.Revision == old.Revision) || (u.UpdateId == newer.UpdateId && u.Revision == newer.Revision))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            string Name(Guid id, int revision) => updates.FirstOrDefault(u => u.UpdateId == id && u.Revision == revision) is { } u ? Describe(u.KbArticle, u.Title, id, revision) : id.ToString("D");
            await audit.WriteAsync(RuleActor, "Rollout.Superseded", Name(old.UpdateId, old.Revision), "superseded by " + Name(newer.UpdateId, newer.Revision), cancellationToken).ConfigureAwait(false);
            LogSuperseded(old.UpdateId, old.Revision, newer.UpdateId, newer.Revision);
            stopped++;
        }

        return stopped;
    }

    /// <summary>Marks withdrawn updates now (after an update report) and saves; stops their rollouts unless only notifications are configured.</summary>
    public async Task CheckWithdrawnAsync(CancellationToken cancellationToken)
    {
        if (await MarkWithdrawnAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Detects updates Microsoft probably withdrew (Marc, 2026-10-22): servers that had the update released report that
    /// Windows Update no longer knows it (neither offered, hidden nor installed), no approved server offers it any more,
    /// and nothing explains it: no known superseding update, not installed on those servers (inventory, SUSyphos or the
    /// Windows Update history, since drivers vanish from the search once installed), and no newer update with the same KB
    /// on them (definition updates get a new update ID per version, often without supersedence; such a pair is recorded
    /// as supersedence, Marc 2026-10-23). Such an update is marked, and its rollout stops unless the setting says "notify".
    /// A mark that turns out wrong goes away, and a rollout it stopped resumes. Does not save; returns the number of changes.
    /// </summary>
    private async Task<int> MarkWithdrawnAsync(CancellationToken cancellationToken)
    {
        var approvedIds = await db.Clients.Where(c => c.State == RegistrationState.Approved).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var notOffered = await db.Releases.AsNoTracking()
            .Where(r => r.NotOfferedAtUtc != null && approvedIds.Contains(r.ClientId))
            .Select(r => new { r.ClientId, r.UpdateId, r.Revision })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var marked = await db.Updates.Where(u => u.WithdrawnAtUtc != null).Select(u => new { u.UpdateId, u.Revision }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var keys = notOffered.Select(r => (r.UpdateId, r.Revision)).Concat(marked.Select(u => (u.UpdateId, u.Revision))).Distinct().ToList();
        if (keys.Count == 0)
        {
            return 0;
        }

        var ids = keys.Select(k => k.UpdateId).Distinct().ToList();
        var updates = await db.Updates.Where(u => ids.Contains(u.UpdateId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var offered = (await db.ClientAvailableUpdates.AsNoTracking()
                .Where(a => approvedIds.Contains(a.ClientId) && ids.Contains(a.UpdateId))
                .Select(a => a.UpdateId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet();
        var superseded = (await db.UpdateSupersedence.AsNoTracking()
                .Where(s => ids.Contains(s.SupersededUpdateId) && s.UpdateId != s.SupersededUpdateId)
                .Select(s => s.SupersededUpdateId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet();
        var servers = notOffered.Select(r => r.ClientId).Distinct().ToList();
        var installed = await InstallEvidence.InstalledAsync(db, servers, ids, cancellationToken).ConfigureAwait(false);

        // Newer updates with the same KB that these servers know (offered or installed).
        var kbs = updates.Where(u => !string.IsNullOrEmpty(u.KbArticle)).Select(u => u.KbArticle!).Distinct().ToList();
        var sameKb = kbs.Count == 0
            ? []
            : await db.Updates.AsNoTracking()
                .Where(u => u.KbArticle != null && kbs.Contains(u.KbArticle) && !ids.Contains(u.UpdateId))
                .Select(u => new SameKbUpdate(u.UpdateId, u.Revision, u.KbArticle!, u.FirstSeenAtUtc))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var sameKbIds = sameKb.Select(u => u.UpdateId).Distinct().ToList();
        var knownOnServer = (await db.ClientAvailableUpdates.AsNoTracking()
                .Where(a => servers.Contains(a.ClientId) && sameKbIds.Contains(a.UpdateId))
                .Select(a => new { a.ClientId, a.UpdateId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(a => (a.ClientId, a.UpdateId))
            .Concat(await InstallEvidence.InstalledAsync(db, servers, sameKbIds, cancellationToken).ConfigureAwait(false))
            .ToHashSet();
        var pairs = (await db.UpdateSupersedence.AsNoTracking()
                .Where(s => sameKbIds.Contains(s.UpdateId) && ids.Contains(s.SupersededUpdateId))
                .Select(s => new { s.UpdateId, s.Revision, s.SupersededUpdateId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(s => (s.UpdateId, s.Revision, s.SupersededUpdateId))
            .ToHashSet();

        var stop = await db.Settings.AsNoTracking().Where(s => s.Key == SettingEntity.WithdrawnAction).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) != "notify";
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var changed = 0;
        foreach (var (updateId, revision) in keys)
        {
            var update = updates.FirstOrDefault(u => u.UpdateId == updateId && u.Revision == revision);
            if (update is null)
            {
                continue;
            }

            var unexplained = 0;
            if (!offered.Contains(updateId) && !superseded.Contains(updateId))
            {
                foreach (var server in notOffered.Where(r => r.UpdateId == updateId && r.Revision == revision).Select(r => r.ClientId).Distinct())
                {
                    if (installed.Contains((server, updateId)))
                    {
                        continue;
                    }

                    var newer = sameKb.FirstOrDefault(u => u.KbArticle == update.KbArticle && u.FirstSeenAtUtc >= update.FirstSeenAtUtc && knownOnServer.Contains((server, u.UpdateId)));
                    if (newer is null)
                    {
                        unexplained++;
                    }
                    else if (pairs.Add((newer.UpdateId, newer.Revision, updateId)))
                    {
                        db.UpdateSupersedence.Add(new UpdateSupersedenceEntity { UpdateId = newer.UpdateId, Revision = newer.Revision, SupersededUpdateId = updateId });
                        changed++;
                    }
                }
            }

            var rollout = await db.Rollouts.SingleOrDefaultAsync(r => r.UpdateId == updateId && r.Revision == revision, cancellationToken).ConfigureAwait(false);
            if (unexplained == 0)
            {
                if (update.WithdrawnAtUtc is null)
                {
                    continue;
                }

                // Not withdrawn after all: the mark goes, and a rollout the mark stopped runs again.
                update.WithdrawnAtUtc = null;
                changed++;
                if (rollout is { State: RolloutState.Stopped, StoppedAsWithdrawn: true } && update.BlockedAtUtc is null)
                {
                    rollout.State = RolloutState.Active;
                    rollout.StoppedAsWithdrawn = false;
                    await audit.WriteAsync(RuleActor, "Rollout.Resumed", Describe(update.KbArticle, update.Title, update.UpdateId, update.Revision), "not withdrawn after all: installed, offered again or replaced by a newer update", cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (update.WithdrawnAtUtc is not null)
            {
                continue;
            }

            update.WithdrawnAtUtc = now;
            changed++;
            if (stop && rollout is { State: RolloutState.Active })
            {
                rollout.State = RolloutState.Stopped;
                rollout.StoppedAsWithdrawn = true;
                await audit.WriteAsync(RuleActor, "Rollout.Withdrawn", Describe(update.KbArticle, update.Title, update.UpdateId, update.Revision), string.Create(CultureInfo.InvariantCulture, $"probably withdrawn by Microsoft: Windows Update no longer knows it on {unexplained} servers that do not have it installed, and no server offers it"), cancellationToken).ConfigureAwait(false);
            }

            LogWithdrawn(update.UpdateId, update.Revision, unexplained, stop && rollout is not null);
        }

        return changed;
    }

    private sealed record SameKbUpdate(Guid UpdateId, int Revision, string KbArticle, DateTime FirstSeenAtUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update {UpdateId} revision {Revision} looks withdrawn by Microsoft ({Servers} servers no longer know it); rollout stopped: {Stopped}.")]
    private partial void LogWithdrawn(Guid updateId, int revision, int servers, bool stopped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rollout of {UpdateId} revision {Revision} stopped: superseded by {NewerId} revision {NewerRevision}, which reached every ring it had reached.")]
    private partial void LogSuperseded(Guid updateId, int revision, Guid newerId, int newerRevision);

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
