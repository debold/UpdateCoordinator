using Microsoft.Extensions.Options;

using SUSyphos.Agent.Wua;

namespace SUSyphos.Agent.Updates;

/// <summary>
/// Searches the update source for updates that are not installed. Throws <see cref="WuaException"/> on failure.
/// </summary>
internal interface IAvailableUpdateSearch
{
    Task<IReadOnlyList<WuaUpdate>> SearchAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Of the given updates, those Windows Update knows neither as available, hidden nor installed: looked up by ID
    /// and revision at the same source as the search. Throws <see cref="WuaException"/> on failure.
    /// </summary>
    Task<IReadOnlyList<(Guid UpdateId, int Revision)>> FindUnknownAsync(IReadOnlyCollection<(Guid UpdateId, int Revision)> updates, CancellationToken cancellationToken);
}

internal sealed class WuaAvailableUpdateSearch(IOptions<UpdateOptions> options, UpdateServiceSelection selection) : IAvailableUpdateSearch
{
    /// <summary>Every update type Windows Update offers, software and drivers alike (no Type criterion).</summary>
    internal const string AvailableCriteria = "IsInstalled=0 and IsHidden=0";

    public Task<IReadOnlyList<WuaUpdate>> SearchAsync(CancellationToken cancellationToken) =>
        // Online search against the configured source; synchronous COM, so it runs off the caller's thread.
        Task.Run<IReadOnlyList<WuaUpdate>>(
            () => WuaSearch.Search(AvailableCriteria, online: true, serverSelection: (int)options.Value.Source, selection.ServiceId),
            cancellationToken);

    public Task<IReadOnlyList<(Guid UpdateId, int Revision)>> FindUnknownAsync(IReadOnlyCollection<(Guid UpdateId, int Revision)> updates, CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<(Guid, int)>>([]);
        }

        return Task.Run<IReadOnlyList<(Guid, int)>>(
            () =>
            {
                var found = WuaSearch.Search(LookupCriteria(updates), online: true, serverSelection: (int)options.Value.Source, selection.ServiceId)
                    .Select(u => (Guid.TryParse(u.UpdateId, out var id) ? id : Guid.Empty, u.Revision))
                    .ToHashSet();
                return [.. updates.Where(u => !found.Contains(u))];
            },
            cancellationToken);
    }

    private static readonly string[] LookupStates = ["IsInstalled=0 and IsHidden=0", "IsInstalled=0 and IsHidden=1", "IsInstalled=1 and IsHidden=0", "IsInstalled=1 and IsHidden=1"];

    /// <summary>Each update by ID and revision, whether installed or not and hidden or not (WUA only combines with "or" at the top level).</summary>
    internal static string LookupCriteria(IEnumerable<(Guid UpdateId, int Revision)> updates) =>
        string.Join(" or ", updates.SelectMany(u => LookupStates
            .Select(state => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"(UpdateID='{u.UpdateId:D}' and RevisionNumber={u.Revision} and {state})"))));
}
