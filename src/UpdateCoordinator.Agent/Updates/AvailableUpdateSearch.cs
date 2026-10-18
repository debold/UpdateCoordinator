using Microsoft.Extensions.Options;

using UpdateCoordinator.Agent.Wua;

namespace UpdateCoordinator.Agent.Updates;

/// <summary>
/// Searches the update source for updates that are not installed. Throws <see cref="WuaException"/> on failure.
/// </summary>
internal interface IAvailableUpdateSearch
{
    Task<IReadOnlyList<WuaUpdate>> SearchAsync(CancellationToken cancellationToken);
}

internal sealed class WuaAvailableUpdateSearch(IOptions<UpdateOptions> options) : IAvailableUpdateSearch
{
    private const string AvailableCriteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";

    public Task<IReadOnlyList<WuaUpdate>> SearchAsync(CancellationToken cancellationToken) =>
        // Online search against the configured source; synchronous COM, so it runs off the caller's thread.
        Task.Run<IReadOnlyList<WuaUpdate>>(
            () => WuaSearch.Search(AvailableCriteria, online: true, serverSelection: (int)options.Value.Source),
            cancellationToken);
}
