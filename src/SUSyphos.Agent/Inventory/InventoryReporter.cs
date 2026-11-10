using SUSyphos.Agent.Communication;
using SUSyphos.Agent.Identity;
using SUSyphos.Contracts.Agents;

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

    /// <summary>When the next collection is due; null if it runs with the next cycle.</summary>
    DateTimeOffset? NextCollectionAt { get; }
}

internal sealed partial class InventoryReporter(
    InventoryCollector collector,
    InventoryScheduler scheduler,
    ICentralServerClient client,
    Status.ActivityTracker activity,
    ILogger<InventoryReporter> logger) : IInventoryReporter
{
    public DateTimeOffset? NextCollectionAt => scheduler.NextCollectionAt;

    public async Task<bool> ReportIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        if (!scheduler.IsCollectionDue)
        {
            return true;
        }

        InventoryRequest inventory;
        using (activity.Begin(AgentPhase.Inventory))
        {
            inventory = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
        }

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
        var packages = inventory.InstalledSoftware?.Count(s => s.PackageId is not null);
        var softwareState = inventory.Software is { } support ? support.State.ToString() : "not reported";
        LogSent(inventory.InstalledUpdates?.Count, inventory.Roles.Count, inventory.InstalledSoftware?.Count, packages, softwareState);
        if (inventory.UpdateSource.UsesWsus)
        {
            LogWsus(inventory.UpdateSource.WsusServer);
        }

        return true;
    }

    public void Reset() => scheduler.Reset();

    [LoggerMessage(Level = LogLevel.Information, Message = "Inventory sent ({Updates} installed updates, {Roles} roles, {Programs} programs of which WinGet knows {Packages}, WinGet {SoftwareState}).")]
    private partial void LogSent(int? updates, int roles, int? programs, int? packages, string softwareState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A WSUS server is configured by policy ({Server}); WSUS is not the intended update source.")]
    private partial void LogWsus(string? server);
}
