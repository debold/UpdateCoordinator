using SUSyphos.TestSupport;

namespace SUSyphos.UiTests;

/// <summary>
/// A second central server with the clean showcase lab (<see cref="ShowcaseData"/>) for the screenshots of the help and the
/// README, so they show realistic data instead of the edge cases the layout tests need.
/// </summary>
public sealed class ShowcaseFixture : UiFixture
{
    protected override Task<Guid> SeedAsync(KestrelServerFactory server) => ShowcaseData.SeedAsync(server);
}
