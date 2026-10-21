using SUSyphos.Server.Approval;

namespace SUSyphos.TestSupport;

/// <summary>Stands in for Active Directory in every test server, so no test ever queries a real directory.</summary>
internal sealed class TestDirectory : IDirectoryLookup
{
    public List<DirectoryGroup> Groups { get; } = [];

    public Task<ComputerAccountState> FindComputerAsync(string domain, string hostName, CancellationToken cancellationToken) =>
        Task.FromResult(ComputerAccountState.NotFound);

    public Task<IReadOnlyList<DirectoryComputer>> ListServersAsync(string domain, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DirectoryComputer>>([]);

    public Task<IReadOnlyList<DirectoryGroup>> FindGroupsAsync(string domain, string term, int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DirectoryGroup>>([.. Groups.Where(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(max)]);
}
