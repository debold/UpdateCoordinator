using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>
/// Stores install reports as installation history.
/// </summary>
internal sealed partial class InstallationService(ServerDbContext db, ClientActivityLog activity, TimeProvider timeProvider, ILogger<InstallationService> logger)
{
    public const string SUSyphosSource = "SUSyphos";

    /// <summary>Returns false if the client no longer exists.</summary>
    public async Task<bool> StoreAsync(Guid clientId, InstallReport report, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return false;
        }

        // A buffered report may arrive twice (lost acknowledgement): store each run only once.
        if (await db.Installations.AnyAsync(i => i.ClientId == clientId && i.RunId == report.RunId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // Only results for updates released to this client are accepted, so a client cannot write arbitrary history.
        var released = (await db.Releases
                .Where(r => r.ClientId == clientId)
                .Select(r => new { r.UpdateId, r.Revision })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(r => (r.UpdateId, r.Revision))
            .ToHashSet();

        var accepted = report.Results.Where(r => released.Contains((r.UpdateId, r.Revision))).DistinctBy(r => (r.UpdateId, r.Revision)).ToList();
        var ignored = report.Results.Count - accepted.Count;
        if (ignored > 0)
        {
            LogIgnoredResults(clientId, ignored);
        }

        // The agent clock is used only for the duration; the receive time anchors the run.
        var finished = timeProvider.GetUtcNow().UtcDateTime;
        var duration = report.FinishedAt - report.StartedAt;
        var started = duration > TimeSpan.Zero && duration < TimeSpan.FromDays(1) ? finished - duration : finished;

        db.Installations.AddRange(accepted.Select(result => new InstallationEntity
        {
            ClientId = clientId,
            RunId = report.RunId,
            UpdateId = result.UpdateId,
            Revision = result.Revision,
            ResultCode = (int)result.ResultCode,
            HResult = result.HResult,
            RebootRequired = result.RebootRequired,
            StartedAtUtc = started,
            FinishedAtUtc = finished,
            Source = SUSyphosSource,
        }));

        client.LastInstallAtUtc = finished;
        client.RebootPending = accepted.Any(r => r.RebootRequired);
        client.OsVersion = report.OsVersionAfter ?? client.OsVersion;
        if (report.PostInstallChecks.Count > 0)
        {
            client.HealthChecks = [.. report.PostInstallChecks.Select(check => new HealthCheckEntry { Name = check.Name, Status = (int)check.Status, Detail = check.Detail })];
        }

        foreach (var failed in accepted.Where(r => r.ResultCode is not (InstallResultCode.Succeeded or InstallResultCode.SucceededWithErrors)))
        {
            LogInstallFailed(clientId, failed.UpdateId, failed.Revision, failed.HResult, HResultCatalog.Describe(failed.HResult));
        }

        var succeeded = accepted.Count(r => r.ResultCode is InstallResultCode.Succeeded or InstallResultCode.SucceededWithErrors);
        activity.Add(clientId, ClientActivityKind.Installation, string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Installation finished: {succeeded} succeeded, {accepted.Count - succeeded} not successful{(client.RebootPending ? ", reboot required" : string.Empty)}."));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {ClientId}: {Count} install results for updates that are not released to it were ignored.")]
    private partial void LogIgnoredResults(Guid clientId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {ClientId}: installation of {UpdateId} revision {Revision} failed with 0x{HResult:X8} ({Description}).")]
    private partial void LogInstallFailed(Guid clientId, Guid updateId, int revision, int hresult, string description);
}
