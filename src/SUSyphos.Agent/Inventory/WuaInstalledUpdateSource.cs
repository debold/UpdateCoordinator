using SUSyphos.Agent.Wua;
using SUSyphos.Contracts.Agents;

namespace SUSyphos.Agent.Inventory;

/// <summary>
/// Reads the installed updates. Returns null if they cannot be read (e.g. Windows Update service disabled).
/// </summary>
internal interface IInstalledUpdateSource
{
    Task<IReadOnlyList<WuaUpdate>?> GetInstalledUpdatesAsync(CancellationToken cancellationToken);

    /// <summary>Windows Update history of the last <see cref="UpdateHistoryEntry.HistoryDays"/> days; null if it cannot be read.</summary>
    Task<IReadOnlyList<UpdateHistoryEntry>?> GetHistoryAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Installed updates from the local WUA cache (Online = false), so no internet access is needed.
/// </summary>
internal sealed partial class WuaInstalledUpdateSource(TimeProvider timeProvider, ILogger<WuaInstalledUpdateSource> logger) : IInstalledUpdateSource
{
    private const string InstalledCriteria = "IsInstalled=1 and IsHidden=0";

    public Task<IReadOnlyList<WuaUpdate>?> GetInstalledUpdatesAsync(CancellationToken cancellationToken) =>
        // The search is synchronous COM; run it off the caller's thread. It cannot be cancelled once started.
        Task.Run<IReadOnlyList<WuaUpdate>?>(
            () =>
            {
                try
                {
                    return WuaSearch.Search(InstalledCriteria, online: false, serverSelection: 0);
                }
                catch (WuaException ex)
                {
                    LogUnavailable(ex.Message);
                    return null;
                }
            },
            cancellationToken);

    public Task<IReadOnlyList<UpdateHistoryEntry>?> GetHistoryAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<UpdateHistoryEntry>?>(
            () =>
            {
                try
                {
                    var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-UpdateHistoryEntry.HistoryDays);
                    return WireFormat.ToHistory(WuaHistory.Query(since, UpdateHistoryEntry.MaxEntries));
                }
                catch (WuaException ex)
                {
                    LogHistoryUnavailable(ex.Message);
                    return null;
                }
            },
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Windows Update history cannot be read ({Reason}); the inventory is sent without it.")]
    private partial void LogHistoryUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Installed updates cannot be read ({Reason}); the inventory is sent without them.")]
    private partial void LogUnavailable(string reason);
}
