using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Rollouts;

/// <summary>When updates leave the lists and the database. Bound from the "Updates" configuration section.</summary>
internal sealed class UpdateArchiveOptions
{
    public const string SectionName = "Updates";

    /// <summary>An update no approved server reported as missing for this long is archived (hidden from the lists).</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "730.00:00:00")]
    public TimeSpan ArchiveAfter { get; set; } = TimeSpan.FromDays(90);

    /// <summary>An archived update is deleted with its rollout and history this long after it was archived.</summary>
    [Range(typeof(TimeSpan), "30.00:00:00", "3650.00:00:00")]
    public TimeSpan DeleteAfter { get; set; } = TimeSpan.FromDays(365);

    /// <summary>How often the archive check runs.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>
/// Archives updates that no server needs anymore (docs/04-central-server.md, update lifecycle) and deletes them a year
/// later. An archived update that a server reports as missing again comes back into the lists. An update with an open
/// release that some server still has to install is never archived.
/// </summary>
internal sealed partial class UpdateArchive(ServerDbContext db, IOptions<UpdateArchiveOptions> options, TimeProvider timeProvider, ILogger<UpdateArchive> logger)
{
    public async Task<(int Archived, int Deleted)> RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var archiveBefore = now - options.Value.ArchiveAfter;
        var deleteBefore = now - options.Value.DeleteAfter;

        var approved = db.Clients.Where(c => c.State == RegistrationState.Approved).Select(c => c.Id);
        var candidates = await db.Updates
            .Where(u => u.ArchivedAtUtc == null && (u.LastNeededAtUtc ?? u.FirstSeenAtUtc) < archiveBefore)
            .Where(u => !db.ClientAvailableUpdates.Any(a => a.UpdateId == u.UpdateId && a.Revision == u.Revision && approved.Contains(a.ClientId)))
            .Where(u => !db.Releases.Any(r => r.UpdateId == u.UpdateId && r.Revision == u.Revision
                && !db.Installations.Any(i => i.ClientId == r.ClientId && i.UpdateId == r.UpdateId && i.Revision == r.Revision)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var update in candidates)
        {
            update.ArchivedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var expired = await db.Updates
            .Where(u => u.ArchivedAtUtc != null && u.ArchivedAtUtc < deleteBefore)
            .Select(u => new { u.UpdateId, u.Revision })
            .Take(500)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var update in expired)
        {
            // Everything that refers to the update goes with it; rollout ring states cascade with the rollout.
            await db.Releases.Where(r => r.UpdateId == update.UpdateId && r.Revision == update.Revision).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.ClientAvailableUpdates.Where(a => a.UpdateId == update.UpdateId && a.Revision == update.Revision).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.ClientUpdates.Where(c => c.UpdateId == update.UpdateId && c.Revision == update.Revision).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Installations.Where(i => i.UpdateId == update.UpdateId && i.Revision == update.Revision).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Rollouts.Where(r => r.UpdateId == update.UpdateId && r.Revision == update.Revision).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Updates.Where(u => u.UpdateId == update.UpdateId && u.Revision == update.Revision).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (candidates.Count > 0 || expired.Count > 0)
        {
            LogRun(candidates.Count, expired.Count);
        }

        return (candidates.Count, expired.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Archived {Archived} updates no server needs anymore and deleted {Deleted} updates archived for over the retention time.")]
    private partial void LogRun(int archived, int deleted);
}

/// <summary>Runs the update archive periodically.</summary>
internal sealed partial class UpdateArchiveWorker(
    IServiceScopeFactory scopes,
    IOptionsMonitor<UpdateArchiveOptions> options,
    TimeProvider timeProvider,
    ILogger<UpdateArchiveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CurrentValue.Interval, timeProvider, stoppingToken).ConfigureAwait(false);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<UpdateArchive>().RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The update archive check failed; it runs again with the next interval.")]
    private partial void LogFailed(Exception exception);
}
