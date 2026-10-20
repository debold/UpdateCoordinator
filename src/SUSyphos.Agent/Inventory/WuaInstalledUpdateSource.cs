using SUSyphos.Agent.Wua;

namespace SUSyphos.Agent.Inventory;

/// <summary>
/// Reads the installed updates. Returns null if they cannot be read (e.g. Windows Update service disabled).
/// </summary>
internal interface IInstalledUpdateSource
{
    Task<IReadOnlyList<WuaUpdate>?> GetInstalledUpdatesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Installed updates from the local WUA cache (Online = false), so no internet access is needed.
/// </summary>
internal sealed partial class WuaInstalledUpdateSource(ILogger<WuaInstalledUpdateSource> logger) : IInstalledUpdateSource
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Installed updates cannot be read ({Reason}); the inventory is sent without them.")]
    private partial void LogUnavailable(string reason);
}
