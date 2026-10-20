using Microsoft.Extensions.Options;

using SUSyphos.Agent.Wua;

namespace SUSyphos.Agent.Updates;

/// <summary>
/// Searches the update source for updates that are not installed. Throws <see cref="WuaException"/> on failure.
/// </summary>
internal interface IAvailableUpdateSearch
{
    Task<IReadOnlyList<WuaUpdate>> SearchAsync(CancellationToken cancellationToken);
}

internal sealed class WuaAvailableUpdateSearch(IOptions<UpdateOptions> options) : IAvailableUpdateSearch
{
    /// <summary>Every update type Windows Update offers, software and drivers alike (no Type criterion).</summary>
    internal const string AvailableCriteria = "IsInstalled=0 and IsHidden=0";

    public Task<IReadOnlyList<WuaUpdate>> SearchAsync(CancellationToken cancellationToken) =>
        // Online search against the configured source; synchronous COM, so it runs off the caller's thread.
        Task.Run<IReadOnlyList<WuaUpdate>>(
            () => WuaSearch.Search(AvailableCriteria, online: true, serverSelection: (int)options.Value.Source),
            cancellationToken);
}
