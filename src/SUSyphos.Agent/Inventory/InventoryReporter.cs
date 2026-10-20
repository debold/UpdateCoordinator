using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Identity;

namespace SUSyphos.Agent.Inventory;

/// <summary>
/// Inventory step of the main loop.
/// </summary>
internal interface IInventoryReporter
{
    /// <summary>Collects and sends the inventory if due. Returns false if the central server rejected the credentials.</summary>
    Task<bool> ReportIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken);

    /// <summary>Forces a full inventory with the next cycle (e.g. after a new registration).</summary>
    void Reset();
}

internal sealed partial class InventoryReporter(
    InventoryCollector collector,
    InventoryScheduler scheduler,
    ICentralServerClient client,
    ILogger<InventoryReporter> logger) : IInventoryReporter
{
    public async Task<bool> ReportIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        if (!scheduler.IsCollectionDue)
        {
            return true;
        }

        var inventory = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
        if (!scheduler.ShouldSend(inventory))
        {
            return true;
        }

        bool accepted;
        try
        {
            accepted = await client.SendInventoryAsync(identity, inventory, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Not delivered: try again with the next successful cycle instead of waiting for the next collection.
            scheduler.MarkSendFailed();
            throw;
        }

        if (!accepted)
        {
            scheduler.MarkSendFailed();
            return false;
        }

        scheduler.MarkSent();
        LogSent(inventory.InstalledUpdates?.Count, inventory.Roles.Count);
        if (inventory.UpdateSource.UsesWsus)
        {
            LogWsus(inventory.UpdateSource.WsusServer);
        }

        return true;
    }

    public void Reset() => scheduler.Reset();

    [LoggerMessage(Level = LogLevel.Information, Message = "Inventory sent ({Updates} installed updates, {Roles} roles).")]
    private partial void LogSent(int? updates, int roles);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A WSUS server is configured by policy ({Server}); WSUS is not the intended update source.")]
    private partial void LogWsus(string? server);
}
