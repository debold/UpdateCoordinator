using Microsoft.EntityFrameworkCore;

using SUSyphos.Contracts.Agents;
using SUSyphos.Server.Assignment;
using SUSyphos.Server.Data;

namespace SUSyphos.Server.Agents;

/// <summary>
/// Stores an agent inventory: client attributes, the installed updates of the client and new catalog entries.
/// </summary>
internal sealed partial class InventoryService(ServerDbContext db, UpdateCatalog catalog, AssignmentService assignment, TimeProvider timeProvider, ILogger<InventoryService> logger)
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

        // Only IDs this central server knows are kept, so the next window can be calculated.
        client.TimeZoneId = inventory.TimeZoneId is { } zone && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _) ? zone : null;
        client.OsProductName = inventory.OperatingSystem.ProductName;
        client.OsEditionId = inventory.OperatingSystem.EditionId;
        client.OsDisplayVersion = inventory.OperatingSystem.DisplayVersion;
        client.OsVersion = inventory.OperatingSystem.Version;
        client.Roles = [.. inventory.Roles.Distinct(StringComparer.OrdinalIgnoreCase)];
        client.UsesWsus = inventory.UpdateSource.UsesWsus;
        client.WsusServer = inventory.UpdateSource.WsusServer;
        client.AutoUpdateMode = inventory.UpdateSource.AutomaticUpdates?.Mode;
        client.AutomaticUpdatesByPolicy = inventory.UpdateSource.AutomaticUpdates?.ByPolicy == true;
        client.AutomaticUpdatesManagedByAgent = inventory.UpdateSource.AutomaticUpdates?.ManagedByAgent == true;
        client.AutomaticUpdatesBypass = inventory.UpdateSource.AutomaticUpdates?.BypassesCoordination() == true;
        client.InventoryAtUtc = now;

        // Null means "could not be read": keep the previous list instead of wiping it.
        if (inventory.InstalledUpdates is { } installed)
        {
            await ReplaceInstalledUpdatesAsync(clientId, installed, now, cancellationToken).ConfigureAwait(false);
        }

        if (inventory.UpdateHistory is { } history)
        {
            await ReplaceHistoryAsync(clientId, history, now, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Roles, domain and host name feed the assignment rules.
        await assignment.RecalculateAsync(clientId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ReplaceInstalledUpdatesAsync(Guid clientId, IReadOnlyList<InstalledUpdate> installed, DateTime now, CancellationToken cancellationToken)
    {
        var reported = installed.DistinctBy(u => (u.UpdateId, u.Revision)).ToList();

        await catalog.EnsureAsync(reported, now, cancellationToken).ConfigureAwait(false);

        await db.ClientUpdates.Where(cu => cu.ClientId == clientId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.ClientUpdates.AddRange(reported.Select(u => new ClientUpdateEntity
        {
            ClientId = clientId,
            UpdateId = u.UpdateId,
            Revision = u.Revision,
            ReportedAtUtc = now,
        }));
    }

    /// <summary>
    /// Replaces the reported Windows Update history. The agent clock is not trusted: entries dated in the future are
    /// dropped, so they cannot pin themselves to the top of the list.
    /// </summary>
    private async Task ReplaceHistoryAsync(Guid clientId, IReadOnlyList<UpdateHistoryEntry> history, DateTime now, CancellationToken cancellationToken)
    {
        await db.ClientUpdateHistory.Where(h => h.ClientId == clientId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.ClientUpdateHistory.AddRange(history
            .Where(h => h.Date.UtcDateTime <= now.AddHours(1) && Enum.IsDefined(h.Operation) && Enum.IsDefined(h.ResultCode))
            .Select(h => new ClientUpdateHistoryEntity
            {
                ClientId = clientId,
                UpdateId = h.UpdateId,
                Revision = h.Revision,
                Title = h.Title,
                DateUtc = h.Date.UtcDateTime,
                Operation = h.Operation,
                ResultCode = h.ResultCode,
                HResult = h.HResult,
                ClientApplicationId = h.ClientApplicationId,
            }));
    }

    /// <summary>Host name and domain are attributes of the GUID: changes are logged but need no new approval (docs/03, 3.1).</summary>
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
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {ClientId}: {Attribute} changed from {Old} to {New}.")]
    private partial void LogAttributeChanged(Guid clientId, string attribute, string? old, string? @new);
}
