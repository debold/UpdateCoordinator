using Microsoft.EntityFrameworkCore;

using UpdateCoordinator.Contracts.Agents;
using UpdateCoordinator.Server.Data;

namespace UpdateCoordinator.Server.Agents;

/// <summary>
/// Stores an agent inventory: client attributes, the installed updates of the client and new catalog entries.
/// </summary>
internal sealed partial class InventoryService(ServerDbContext db, TimeProvider timeProvider, ILogger<InventoryService> logger)
{
    /// <summary>Returns false if the client no longer exists.</summary>
    public async Task<bool> StoreAsync(Guid clientId, InventoryRequest inventory, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return false;
        }

        LogIdentityChanges(client, inventory);
        client.HostName = inventory.HostName;
        client.DomainName = inventory.DomainName;
        client.ComputerSid = inventory.ComputerSid;
        client.OsProductName = inventory.OperatingSystem.ProductName;
        client.OsEditionId = inventory.OperatingSystem.EditionId;
        client.OsDisplayVersion = inventory.OperatingSystem.DisplayVersion;
        client.OsVersion = inventory.OperatingSystem.Version;
        client.Roles = [.. inventory.Roles.Distinct(StringComparer.OrdinalIgnoreCase)];
        client.UsesWsus = inventory.UpdateSource.UsesWsus;
        client.WsusServer = inventory.UpdateSource.WsusServer;
        client.InventoryAtUtc = now;

        // Null means "could not be read": keep the previous list instead of wiping it.
        if (inventory.InstalledUpdates is { } installed)
        {
            await ReplaceInstalledUpdatesAsync(clientId, installed, now, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ReplaceInstalledUpdatesAsync(Guid clientId, IReadOnlyList<InstalledUpdate> installed, DateTime now, CancellationToken cancellationToken)
    {
        var reported = installed.DistinctBy(u => (u.UpdateId, u.Revision)).ToList();

        // Add catalog entries that are not known yet (the catalog is built from agent reports).
        var ids = reported.Select(u => u.UpdateId).Distinct().ToList();
        var known = (await db.Updates
                .Where(u => ids.Contains(u.UpdateId))
                .Select(u => new { u.UpdateId, u.Revision })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(u => (u.UpdateId, u.Revision))
            .ToHashSet();

        db.Updates.AddRange(reported
            .Where(u => !known.Contains((u.UpdateId, u.Revision)))
            .Select(u => new UpdateEntity
            {
                UpdateId = u.UpdateId,
                Revision = u.Revision,
                KbArticle = u.KbArticle,
                Title = u.Title,
                Classification = u.Classification,
                FirstSeenAtUtc = now,
            }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await db.ClientUpdates.Where(cu => cu.ClientId == clientId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.ClientUpdates.AddRange(reported.Select(u => new ClientUpdateEntity
        {
            ClientId = clientId,
            UpdateId = u.UpdateId,
            Revision = u.Revision,
            ReportedAtUtc = now,
        }));
    }

    /// <summary>Host name, domain and SID are attributes of the GUID: changes are logged but need no new approval (docs/03, 3.1).</summary>
    private void LogIdentityChanges(ClientEntity client, InventoryRequest inventory)
    {
        if (!string.Equals(client.HostName, inventory.HostName, StringComparison.OrdinalIgnoreCase))
        {
            LogAttributeChanged(client.Id, "host name", client.HostName, inventory.HostName);
        }

        if (!string.Equals(client.DomainName, inventory.DomainName, StringComparison.OrdinalIgnoreCase))
        {
            LogAttributeChanged(client.Id, "domain", client.DomainName, inventory.DomainName);
        }

        if (client.ComputerSid is not null && !string.Equals(client.ComputerSid, inventory.ComputerSid, StringComparison.OrdinalIgnoreCase))
        {
            LogAttributeChanged(client.Id, "computer SID", client.ComputerSid, inventory.ComputerSid);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {ClientId}: {Attribute} changed from {Old} to {New}.")]
    private partial void LogAttributeChanged(Guid clientId, string attribute, string? old, string? @new);
}
